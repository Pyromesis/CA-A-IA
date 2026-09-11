// CA-A-IA — Cliente HTTP compatible con la API OpenAI de chat completions.
// Lo usan OpenRouter y los modelos locales (LM Studio / Ollama vía /v1). Solo estándares:
// chat completions + SSE + response_format json_object + GET /models. Sin SDKs de proveedor.

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CaAIA.Domain.AI;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure.AI;

/// <summary>
/// Cliente del estándar de facto OpenAI (<c>POST {base}/chat/completions</c>, SSE con
/// <c>stream:true</c>, <c>GET {base}/models</c>). Los errores HTTP se traducen a
/// <see cref="AIProviderException"/> con reintentabilidad explícita.
/// </summary>
public sealed class OpenAICompatibleClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _providerId;
    private readonly Func<CancellationToken, Task<string?>> _apiKeyProvider;
    private readonly ProviderCapabilities _modelCapabilities;
    private readonly bool _sendReasoningEffort;
    private readonly ILogger _log;

    public OpenAICompatibleClient(
        HttpClient http,
        string providerId,
        string baseUrl,
        Func<CancellationToken, Task<string?>> apiKeyProvider,
        ProviderCapabilities modelCapabilities,
        ILogger log,
        bool sendReasoningEffort = false)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _providerId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        _baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).TrimEnd('/');
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _modelCapabilities = modelCapabilities;
        _sendReasoningEffort = sendReasoningEffort;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _http.Timeout = Timeout.InfiniteTimeSpan; // timeouts por petición vía CTS, no globales
    }

    public async Task<AIResponse> CompleteAsync(AIRequest request, TimeSpan defaultTimeout, CancellationToken ct)
    {
        using var body = BuildRequestDocument(request, stream: false);
        using var response = await SendAsync("chat/completions", body, request.Timeout ?? defaultTimeout, ct)
            .ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        ThrowIfError(doc, response.StatusCode, response.Headers);
        return ParseCompletion(doc, request.ModelId);
    }

    public async IAsyncEnumerable<AIStreamChunk> StreamAsync(
        AIRequest request, TimeSpan defaultTimeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var body = BuildRequestDocument(request, stream: true);
        using var response = await SendAsync("chat/completions", body, request.Timeout ?? defaultTimeout, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            ThrowIfError(doc, response.StatusCode, response.Headers);
            response.EnsureSuccessStatusCode();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var toolId = string.Empty;
        var toolName = string.Empty;
        var toolArgs = new StringBuilder();
        var corruptFragments = 0;
        while (true) // EOF = ReadLineAsync() devuelve null (sin EndOfStream: CA2024)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                break;
            }

            JsonDocument evt;
            try
            {
                evt = JsonDocument.Parse(payload);
            }
            catch (JsonException)
            {
                // Fragmento corrupto: se salta, el stream continúa (con contador para
                // detectar un stream envenenado que truncaría el contenido en silencio).
                corruptFragments++;
                continue;
            }

            using (evt)
            {
                var delta = evt.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                    ? choices[0].GetProperty("delta") : default;
                if (delta.ValueKind == JsonValueKind.Undefined)
                {
                    continue;
                }

                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString() ?? string.Empty;
                    if (text.Length > 0)
                    {
                        yield return new AIStreamChunk(text);
                    }
                }

                if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in calls.EnumerateArray())
                    {
                        if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        {
                            toolId = id.GetString() ?? toolId;
                        }

                        if (call.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                            {
                                toolName = n.GetString() ?? toolName;
                            }

                            if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                            {
                                var fragment = a.GetString() ?? string.Empty;
                                toolArgs.Append(fragment);
                                yield return new AIStreamChunk(string.Empty,
                                    new AIToolCall(toolId, toolName, fragment));
                            }
                        }
                    }
                }
            }
        }

        if (corruptFragments > 0)
        {
            _log.LogDebug("Stream from {Provider} had {Count} corrupt SSE fragments", _providerId, corruptFragments);
        }

        yield return new AIStreamChunk(string.Empty,
            toolId.Length > 0 ? new AIToolCall(toolId, toolName, toolArgs.ToString()) : null,
            IsFinal: true);
    }

    public async Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
            await AuthorizeAsync(req, ct).ConfigureAwait(false);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Auth: excepción (no catálogo vacío) para que el error sea visible y
                // CheckHealth pueda distinguir "key inválida" de "sin modelos".
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new AIProviderException(_providerId,
                        response.StatusCode == HttpStatusCode.Unauthorized
                            ? AIErrorKind.Authentication : AIErrorKind.Authorization,
                        $"GET models -> {(int)response.StatusCode} {response.StatusCode} (provider {_providerId}).",
                        isRetryable: false);
                }

                _log.LogDebug("GET models -> {Status} (provider {Provider})", response.StatusCode, _providerId);
                return Array.Empty<AIModel>();
            }

            using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<AIModel>();
            }

                var models = new List<AIModel>();
            foreach (var m in data.EnumerateArray())
            {
                if (!m.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var modelId = id.GetString()!;
                var name = m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? modelId : modelId;
                int? context = m.TryGetProperty("context_length", out var c) && c.ValueKind == JsonValueKind.Number
                    ? c.GetInt32() : null;
                var caps = _modelCapabilities;
                if (context is >= 100_000)
                {
                    caps |= ProviderCapabilities.LongContext;
                }

                // OpenRouter marca sus endpoints gratuitos con sufijo ":free".
                var isFree = modelId.EndsWith(":free", StringComparison.OrdinalIgnoreCase);
                models.Add(new AIModel(modelId, name, _providerId, context, caps, IsFree: isFree)
                {
                    SupportedEfforts = ParseSupportedEfforts(m),
                });
            }

            return models;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogDebug(ex, "GET models failed (provider {Provider})", _providerId);
            return Array.Empty<AIModel>();
        }
    }

    /// <summary>
    /// Niveles aceptados según el catálogo (OpenRouter: `reasoning.supported_efforts`,
    /// en orden descendente). Vacío = todos/desconocido.
    /// </summary>
    internal static IReadOnlyList<string> ParseSupportedEfforts(JsonElement model)
    {
        if (model.ValueKind == JsonValueKind.Object
            && model.TryGetProperty("reasoning", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.Object
            && reasoning.TryGetProperty("supported_efforts", out var efforts)
            && efforts.ValueKind == JsonValueKind.Array)
        {
            var list = efforts.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim().ToLowerInvariant())
                .Where(s => s.Length > 0)
                .Distinct()
                .ToList();
            return list;
        }

        return Array.Empty<string>();
    }

    // ---- Construcción y envío ----

    private StringContent BuildRequestDocument(AIRequest request, bool stream)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.ModelId);
            writer.WriteBoolean("stream", stream);
            writer.WriteNumber("temperature", request.Temperature);
            if (request.MaxOutputTokens.HasValue)
            {
                writer.WriteNumber("max_tokens", request.MaxOutputTokens.Value);
            }

            // reasoning.effort es específico de pasarela (verificado en OpenRouter);
            // el resto de servidores OpenAI-compatibles no lo reciben.
            if (_sendReasoningEffort && !string.IsNullOrWhiteSpace(request.ReasoningEffort))
            {
                writer.WritePropertyName("reasoning");
                writer.WriteStartObject();
                writer.WriteString("effort", request.ReasoningEffort);
                writer.WriteEndObject();
            }

            if (request.StructuredOutputSchema is not null)
            {
                writer.WritePropertyName("response_format");
                writer.WriteStartObject();
                writer.WriteString("type", "json_object");
                writer.WriteEndObject();
            }

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in request.Messages)
            {
                WriteMessage(writer, message);
            }

            foreach (var result in request.ToolResults)
            {
                writer.WriteStartObject();
                writer.WriteString("role", "tool");
                writer.WriteString("tool_call_id", result.ToolCallId);
                writer.WriteString("content", result.Content);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (request.Tools.Count > 0)
            {
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (var tool in request.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WritePropertyName("function");
                    writer.WriteStartObject();
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    writer.WriteStartObject();
                    writer.WriteString("type", "object");
                    writer.WritePropertyName("properties");
                    writer.WriteStartObject();
                    foreach (var p in tool.Parameters)
                    {
                        writer.WritePropertyName(p.Name);
                        writer.WriteStartObject();
                        writer.WriteString("type", p.JsonType);
                        writer.WriteString("description", p.Description);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndObject();
                    var required = tool.Parameters.Where(p => p.IsRequired).Select(p => p.Name).ToList();
                    writer.WritePropertyName("required");
                    writer.WriteStartArray();
                    foreach (var r in required)
                    {
                        writer.WriteStringValue(r);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject(); // parameters
                    writer.WriteEndObject(); // function
                    writer.WriteEndObject(); // tool
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json");
    }

    private void WriteMessage(Utf8JsonWriter writer, AIMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", message.Role switch
        {
            AIRole.System => "system",
            AIRole.Assistant => "assistant",
            AIRole.Tool => "tool",
            _ => "user",
        });
        var images = ReadImages(message.Images);
        if (images.Count == 0)
        {
            writer.WriteString("content", message.Content);
        }
        else
        {
            // Contenido multimodal: texto + image_url (requiere modelo con visión).
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", message.Content);
            writer.WriteEndObject();
            foreach (var image in images)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "image_url");
                writer.WritePropertyName("image_url");
                writer.WriteStartObject();
                writer.WriteString("url", $"data:{image.Mime};base64,{image.Base64}");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (message.Role == AIRole.Tool && message.ToolCallId is not null)
        {
            writer.WriteString("tool_call_id", message.ToolCallId);
        }

        if (message is { Role: AIRole.Assistant, ToolCalls.Count: > 0 })
        {
            writer.WritePropertyName("tool_calls");
            writer.WriteStartArray();
            foreach (var call in message.ToolCalls)
            {
                writer.WriteStartObject();
                writer.WriteString("id", call.Id);
                writer.WriteString("type", "function");
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                writer.WriteString("name", call.ToolName);
                writer.WriteString("arguments", call.ArgumentsJson);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private sealed record EncodedImage(string Mime, string Base64);

    /// <summary>Lee imágenes locales (tope 6 MB c/u): lo ilegible se omite, nunca rompe.</summary>
    private List<EncodedImage> ReadImages(IReadOnlyList<string> paths)
    {
        var result = new List<EncodedImage>();
        if (paths is null || paths.Count == 0)
        {
            return result;
        }

        foreach (var path in paths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var info = new FileInfo(path);
                if (!info.Exists || info.Length is <= 0 or > 6 * 1024 * 1024)
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(path);
                var mime = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    _ => "image/png",
                };
                result.Add(new EncodedImage(mime, Convert.ToBase64String(bytes)));
                if (result.Count >= 4)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Skipping unreadable image {Path}", path);
            }
        }

        return result;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string path, HttpContent body, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/{path}") { Content = body };
            await AuthorizeAsync(req, linked.Token).ConfigureAwait(false);
            req.Headers.UserAgent.ParseAdd("CA-A-IA/0.2");
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AIProviderException(_providerId, AIErrorKind.Timeout,
                $"Request timed out after {timeout}.", isRetryable: true);
        }
        catch (HttpRequestException ex)
        {
            throw new AIProviderException(_providerId, AIErrorKind.Network,
                $"Network error: {ex.Message}", isRetryable: true, inner: ex);
        }
    }

    private async Task AuthorizeAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var key = await _apiKeyProvider(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(key))
        {
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new AIProviderException("unknown", AIErrorKind.ProviderInternal,
                $"Invalid JSON from provider (HTTP {(int)response.StatusCode}).", isRetryable: true, inner: ex);
        }
    }

    private void ThrowIfError(JsonDocument doc, HttpStatusCode status, System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        if (!doc.RootElement.TryGetProperty("error", out var error))
        {
            return;
        }

        var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? "Provider error" : "Provider error";
        var kind = status switch
        {
            HttpStatusCode.Unauthorized => AIErrorKind.Authentication,
            HttpStatusCode.Forbidden => AIErrorKind.Authorization,
            HttpStatusCode.NotFound => AIErrorKind.ModelNotFound,
            HttpStatusCode.BadRequest when message.Contains("context", StringComparison.OrdinalIgnoreCase)
                => AIErrorKind.ContextTooLong,
            HttpStatusCode.TooManyRequests => AIErrorKind.RateLimited,
            >= HttpStatusCode.InternalServerError => AIErrorKind.ProviderInternal,
            _ => AIErrorKind.ProviderInternal,
        };
        var retryable = kind is AIErrorKind.RateLimited or AIErrorKind.ProviderInternal
            // 400 genérico (schema, modelo inválido…): no reintentable. Reintentar a
            // ciegas multiplica el coste sin posibilidad de éxito.
            && status != HttpStatusCode.BadRequest;
        throw new AIProviderException(_providerId, kind, message, isRetryable: retryable,
            retryAfter: ParseRetryAfter(headers));
    }

    private static TimeSpan? ParseRetryAfter(System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        try
        {
            if (headers.RetryAfter?.Delta.HasValue == true)
            {
                return headers.RetryAfter.Delta.Value;
            }

            if (headers.RetryAfter?.Date.HasValue == true)
            {
                var delay = headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            }
        }
        catch (FormatException)
        {
        }

        return null;
    }

    private AIResponse ParseCompletion(JsonDocument doc, string modelId)
    {
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new AIProviderException(_providerId, AIErrorKind.ProviderInternal,
                "Empty choices from provider.", isRetryable: true);
        }

        var message = choices[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? string.Empty : string.Empty;
        var finish = choices[0].TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String
            ? f.GetString() : null;

        var calls = new List<AIToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                // Servidor no confiable: todo defensivo, nunca GetProperty directo.
                var id = call.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
                    ? i.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                if (!call.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object)
                {
                    _log.LogDebug("Skipping tool_call without function object (provider {Provider})", _providerId);
                    continue;
                }

                if (!fn.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                {
                    _log.LogDebug("Skipping tool_call without name (provider {Provider})", _providerId);
                    continue;
                }

                calls.Add(new AIToolCall(id,
                    nameEl.GetString() ?? string.Empty,
                    fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String
                        ? a.GetString() ?? "{}" : "{}"));
            }
        }

        var usage = new TokenUsage(0, 0);
        if (doc.RootElement.TryGetProperty("usage", out var u))
        {
            usage = new TokenUsage(
                u.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0,
                u.TryGetProperty("completion_tokens", out var t) ? t.GetInt32() : 0);
        }

        _log.LogDebug("Completion {Model}: {In}+{Out} tokens, {Tools} tool calls, finish={Finish}",
            modelId, usage.PromptTokens, usage.CompletionTokens, calls.Count, finish);
        return new AIResponse(content, calls, modelId, usage, finish);
    }
}
