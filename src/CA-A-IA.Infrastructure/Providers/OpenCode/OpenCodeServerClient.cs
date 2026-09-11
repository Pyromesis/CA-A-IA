// CA-A-IA — Cliente HTTP del servidor OpenCode (`opencode serve`, API documentada en
// opencode.ai/docs/server + tipos del SDK en opencode.ai/docs/sdk).
// Superficie usada (verificada contra la spec OpenAPI v1.18 + servidor real):
// GET /global/health, POST /session (create), POST /session/{id}/message,
// GET /config/providers. Parseo defensivo: lo desconocido se ignora, nunca se inventa.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure.Providers.OpenCode;

/// <summary>Sesión remota de OpenCode.</summary>
public sealed record OpenCodeSession(string Id, string Title);

/// <summary>Respuesta del asistente: texto agregado de las parts + tokens si el servidor los da.</summary>
public sealed record OpenCodeAnswer(
    string Text,
    int InputTokens,
    int OutputTokens,
    IReadOnlyList<OpenCodeServerToolCall>? ToolCalls = null);

/// <summary>
/// Herramienta observada en el servidor (part de mensaje). Todo best-effort:
/// el esquema exacto varía por versión; lo desconocido se ignora, nunca rompe.
/// </summary>
public sealed record OpenCodeServerToolCall(
    string Key,
    string Tool,
    string Title,
    string State);

/// <summary>Modelo ofertado (forma `provider/model` de `opencode models` o catálogo del servidor).</summary>
public sealed record OpenCodeModel(
    string Id,
    string ProviderId,
    string ModelId,
    int? ContextWindow = null,
    bool IsFree = false,
    bool SupportsTools = true,
    bool SupportsVision = false);

public sealed class OpenCodeServerClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _username;
    private readonly Func<CancellationToken, Task<string?>> _passwordProvider;
    private readonly ILogger _log;

    public OpenCodeServerClient(
        HttpClient http, string baseUrl, string? username,
        Func<CancellationToken, Task<string?>> passwordProvider, ILogger log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).TrimEnd('/');
        _username = username;
        _passwordProvider = passwordProvider ?? throw new ArgumentNullException(nameof(passwordProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        ValidateBaseUrl(_baseUrl);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// La credencial Basic solo viaja por loopback en claro o por HTTPS: si la URL
    /// es http:// no-loopback, fallar en construcción en vez de filtrar la clave.
    /// </summary>
    internal static void ValidateBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid OpenCode base URL: '{baseUrl}'.", nameof(baseUrl));
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            var host = uri.Host.Trim('[', ']');
            var isLoopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
                || string.Equals(host, "::1", StringComparison.Ordinal);
            if (!isLoopback && System.Net.IPAddress.TryParse(host, out var ip))
            {
                isLoopback = System.Net.IPAddress.IsLoopback(ip);
            }

            if (!isLoopback)
            {
                throw new ArgumentException(
                    $"Refusing non-loopback http OpenCode URL '{baseUrl}' (credentials would travel in clear).",
                    nameof(baseUrl));
            }
        }
        else if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"Unsupported OpenCode URL scheme '{uri.Scheme}'.", nameof(baseUrl));
        }
    }

    public async Task<(bool Healthy, string Version)> GetHealthAsync(CancellationToken ct)
    {
        using var response = await GetAsync("global/health", TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (false, string.Empty);
        }

        try
        {
            using var doc = await JsonAsync(response, ct).ConfigureAwait(false);
            var healthy = doc.RootElement.TryGetProperty("healthy", out var h) && h.ValueKind == JsonValueKind.True;
            var version = doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty : string.Empty;
            return (healthy, version);
        }
        catch (JsonException)
        {
            return (false, string.Empty);
        }
    }

    public async Task<OpenCodeSession> CreateSessionAsync(string title, string? directory, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { title });
        using var response = await PostAsync("session" + Query(directory), payload, TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);
        using var doc = await EnsureJsonAsync(response, "create session", ct).ConfigureAwait(false);
        var root = UnwrapData(doc.RootElement);
        var id = Str(root, "id") ?? Str(root, "sessionID") ?? Str(root, "sessionId")
            ?? throw new InvalidOperationException("OpenCode create session: missing id.");
        return new OpenCodeSession(id, Str(root, "title") ?? title);
    }

    /// <summary>
    /// Envía el prompt y devuelve la respuesta del asistente (el servidor la incluye en la
    /// respuesta por defecto). Las parts de tipo texto se concatenan en orden.
    /// Ruta verificada contra la spec OpenAPI real (v1.18): POST /session/{id}/message.
    /// Si el modelo devuelve error (p. ej. credenciales del proveedor), se traduce a excepción.
    /// </summary>
    public async Task<OpenCodeAnswer> PromptAsync(
        string sessionId, string text, string? providerId, string? modelId,
        string? directory, TimeSpan timeout, CancellationToken ct, string? variant = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WritePropertyName("parts");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteEndArray();
            if (providerId is not null && modelId is not null)
            {
                w.WritePropertyName("model");
                w.WriteStartObject();
                w.WriteString("providerID", providerId);
                w.WriteString("modelID", modelId);
                w.WriteEndObject();
            }

            if (!string.IsNullOrWhiteSpace(variant))
            {
                w.WriteString("variant", variant);
            }

            w.WriteEndObject();
        }

        using var response = await PostAsync(
            $"session/{Uri.EscapeDataString(sessionId)}/message" + Query(directory),
            Encoding.UTF8.GetString(ms.ToArray()), timeout, ct).ConfigureAwait(false);
        using var doc = await EnsureJsonAsync(response, "prompt", ct).ConfigureAwait(false);
        return ParseAnswer(UnwrapData(doc.RootElement));
    }

    public async Task<IReadOnlyList<OpenCodeModel>> GetProviderModelsAsync(CancellationToken ct)
    {
        using var response = await GetAsync("config/providers", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<OpenCodeModel>();
        }

        try
        {
            using var doc = await JsonAsync(response, ct).ConfigureAwait(false);
            return ParseProviders(UnwrapData(doc.RootElement));
        }
        catch (JsonException ex)
        {
            _log.LogDebug(ex, "OpenCode config/providers: unparsable shape.");
            return Array.Empty<OpenCodeModel>();
        }
    }

    // ---- HTTP ----

    private static string Query(string? directory) =>
        string.IsNullOrWhiteSpace(directory)
            ? string.Empty
            : "?directory=" + Uri.EscapeDataString(directory);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        if (!string.IsNullOrWhiteSpace(_username))
        {
            var password = await _passwordProvider(linked.Token).ConfigureAwait(false) ?? string.Empty;
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{password}"));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
        }

        try
        {
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"OpenCode request timed out after {timeout}.");
        }
    }

    private Task<HttpResponseMessage> GetAsync(string path, TimeSpan timeout, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/{path}"), timeout, ct);

    private Task<HttpResponseMessage> PostAsync(string path, string json, TimeSpan timeout, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/{path}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }, timeout, ct);

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> EnsureJsonAsync(HttpResponseMessage response, string op, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"OpenCode {op} failed (HTTP {(int)response.StatusCode}): {Truncate(body, 300)}");
        }

        try
        {
            return await JsonAsync(response, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"OpenCode {op}: invalid JSON.", ex);
        }
    }

    // ---- Parseo defensivo (SDK: Message { info, parts[] }, Part { type: "text", text }) ----

    internal static JsonElement UnwrapData(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;

    internal static OpenCodeAnswer ParseAnswer(JsonElement root)
    {
        // Formas observadas/documentadas: { info, parts[] } o mensaje directo con parts.
        var node = root;
        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("parts", out _) == false
            && node.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            node = info;
        }

        // Error del modelo (p. ej. credenciales del proveedor): excepción honesta, no texto vacío.
        var err = FindError(root);
        if (err is not null)
        {
            throw new Domain.AI.AIProviderException("opencode",
                Domain.AI.AIErrorKind.ProviderInternal, err, isRetryable: false);
        }

        var sb = new StringBuilder();
        var tools = new List<OpenCodeServerToolCall>();
        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("parts", out var parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    index++;
                    continue;
                }

                var type = Str(part, "type");
                if (type == "text" && Str(part, "text") is { } text)
                {
                    sb.Append(text);
                }

                var tool = ParseServerToolPart(part, $"p{index}");
                if (tool is not null)
                {
                    tools.Add(tool);
                }

                index++;
            }
        }

        // Tokens: varias formas posibles (info.tokens {input,output}); ausente → 0, nunca inventado.
        var (input, output) = (0, 0);
        var tokens = FindTokens(root);
        if (tokens.HasValue)
        {
            input = tokens.Value.Input;
            output = tokens.Value.Output;
        }

        return new OpenCodeAnswer(sb.ToString(), input, output, tools);
    }

    /// <summary>
    /// Lista los mensajes de una sesión con sus parts (para narrar herramientas
    /// mientras el servidor trabaja). 404/versión sin soporte → lista vacía.
    /// </summary>
    public async Task<IReadOnlyList<OpenCodeServerToolCall>> GetSessionToolCallsAsync(
        string sessionId, CancellationToken ct)
    {
        using var response = await GetAsync(
            $"session/{Uri.EscapeDataString(sessionId)}/message?limit=50",
            TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<OpenCodeServerToolCall>();
        }

        try
        {
            using var doc = await JsonAsync(response, ct).ConfigureAwait(false);
            return ParseMessageListTools(UnwrapData(doc.RootElement));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _log.LogDebug(ex, "OpenCode session messages: unparsable shape.");
            return Array.Empty<OpenCodeServerToolCall>();
        }
    }

    /// <summary>Aborta la sesión en el servidor (desatascar su lado al cancelar/timeout). Nunca lanza.</summary>
    public async Task AbortSessionAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            using var response = await PostAsync(
                $"session/{Uri.EscapeDataString(sessionId)}/abort", "{}", TimeSpan.FromSeconds(10), ct)
                .ConfigureAwait(false);
            _log.LogDebug("OpenCode abort {Session}: {Status}", sessionId, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "OpenCode abort {Session} failed (non-fatal).", sessionId);
        }
    }

    /// <summary>Parts de una lista [{info, parts[]}] o de un mensaje suelto.</summary>
    internal static IReadOnlyList<OpenCodeServerToolCall> ParseMessageListTools(JsonElement root)
    {
        var result = new List<OpenCodeServerToolCall>();
        IEnumerable<JsonElement> messages = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : new[] { root };
        foreach (var message in messages)
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var messageId = Str(message, "id") ?? Str(message, "messageID") ?? Str(message, "messageId")
                ?? (message.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                    ? Str(info, "id") ?? Str(info, "messageID") : null)
                ?? "m";
            var node = message;
            if (node.TryGetProperty("info", out var infoObj) && infoObj.ValueKind == JsonValueKind.Object
                && !message.TryGetProperty("parts", out _) && infoObj.TryGetProperty("parts", out _))
            {
                node = infoObj;
            }

            if (!node.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var index = 0;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object)
                {
                    var tool = ParseServerToolPart(part, $"{messageId}#{index}");
                    if (tool is not null)
                    {
                        result.Add(tool);
                    }
                }

                index++;
            }
        }

        return result;
    }

    /// <summary>
    /// Detecta una part de herramienta: type con "tool" o campo "tool"/"name" de
    /// herramienta conocida (read/edit/write/bash/...). Todo lo demás → null.
    /// </summary>
    internal static OpenCodeServerToolCall? ParseServerToolPart(JsonElement part, string fallbackKey)
    {
        var type = Str(part, "type") ?? string.Empty;
        var tool = Str(part, "tool");
        var name = Str(part, "name");
        var isTool = type.Contains("tool", StringComparison.OrdinalIgnoreCase)
            || IsKnownServerTool(tool) || IsKnownServerTool(name);
        if (!isTool)
        {
            return null;
        }

        tool ??= name ?? type;
        var title = Str(part, "title")
            ?? Str(part, "file") ?? Str(part, "path") ?? Str(part, "filename")
            ?? Str(part, "command") ?? Str(part, "pattern") ?? Str(part, "query")
            ?? TryNestedStr(part, "input", "file") ?? TryNestedStr(part, "input", "path")
            ?? TryNestedStr(part, "input", "command") ?? TryNestedStr(part, "input", "pattern")
            ?? TryNestedStr(part, "state", "title") ?? string.Empty;
        var state = TryNestedStr(part, "state", "status")
            ?? Str(part, "status") ?? Str(part, "state") ?? string.Empty;
        var key = Str(part, "id") ?? Str(part, "partID") ?? Str(part, "partId") ?? fallbackKey;
        return new OpenCodeServerToolCall(key, tool.Trim(), title.Trim(), state.Trim().ToLowerInvariant());
    }

    private static bool IsKnownServerTool(string? name) =>
        name is not null && name.Trim().ToLowerInvariant() is
            "read" or "edit" or "write" or "create" or "bash" or "shell" or "exec"
            or "glob" or "grep" or "list" or "patch" or "apply" or "run" or "todo"
            or "webfetch" or "websearch";

    private static string? TryNestedStr(JsonElement el, string outer, string inner) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(outer, out var o) && o.ValueKind == JsonValueKind.Object
            ? Str(o, inner) : null;

    private static (int Input, int Output)? FindTokens(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var prop in new[] { "tokens", "usage" })
        {
            if (root.TryGetProperty(prop, out var t) && t.ValueKind == JsonValueKind.Object)
            {
                var input = Int(t, "input") ?? Int(t, "inputTokens") ?? Int(t, "prompt_tokens") ?? 0;
                var output = Int(t, "output") ?? Int(t, "outputTokens") ?? Int(t, "completion_tokens") ?? 0;
                return (input, output);
            }
        }

        if (root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            return FindTokens(info);
        }

        return null;
    }

    /// <summary>
    /// Error embebido en la respuesta (verificado en vivo: info.error {name, data.message}).
    /// </summary>
    internal static string? FindError(JsonElement root)
    {
        JsonElement node = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("info", out var info)
            && info.ValueKind == JsonValueKind.Object)
        {
            node = info;
        }

        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("error", out var error))
        {
            return null;
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString();
        }

        if (error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                return m.GetString();
            }

            if (error.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("message", out var dm) && dm.ValueKind == JsonValueKind.String)
            {
                return dm.GetString();
            }

            if (error.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            {
                return n.GetString();
            }
        }

        return "Unknown model error.";
    }

    internal static IReadOnlyList<OpenCodeModel> ParseProviders(JsonElement root)
    {
        // Servidor real (verificado v1.18): { providers: [{ id, models: { id: {...} } }] }.
        // También se tolera models como array (otras versiones) y providers como array raíz.
        var result = new List<OpenCodeModel>();
        JsonElement providers = default;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("providers", out var p) && p.ValueKind == JsonValueKind.Array)
            {
                providers = p;
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                providers = root;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            providers = root;
        }

        if (providers.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var provider in providers.EnumerateArray())
        {
            if (provider.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var providerId = Str(provider, "id") ?? Str(provider, "providerID") ?? Str(provider, "name");
            if (string.IsNullOrWhiteSpace(providerId) || !provider.TryGetProperty("models", out var models))
            {
                continue;
            }

            if (models.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in models.EnumerateObject())
                {
                    var model = ParseServerModel(providerId, entry.Name, entry.Value);
                    if (model is not null)
                    {
                        result.Add(model);
                    }
                }
            }
            else if (models.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in models.EnumerateArray())
                {
                    OpenCodeModel? parsed = null;
                    if (model.ValueKind == JsonValueKind.String)
                    {
                        parsed = new OpenCodeModel(
                            $"{providerId}/{model.GetString()}", providerId, model.GetString()!);
                    }
                    else if (model.ValueKind == JsonValueKind.Object)
                    {
                        var modelId = Str(model, "id") ?? Str(model, "modelID");
                        if (!string.IsNullOrWhiteSpace(modelId))
                        {
                            parsed = ParseServerModel(providerId, modelId, model);
                        }
                    }

                    if (parsed is not null)
                    {
                        result.Add(parsed);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Modelo del servidor: { id, name?, cost? {input,output}, limit? {context},
    /// capabilities? {toolcall, input: {image}} }. Gratis = coste 0/0.
    /// </summary>
    internal static OpenCodeModel? ParseServerModel(string providerId, string key, JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var modelId = Str(model, "id") ?? key;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        int? context = null;
        if (model.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Object)
        {
            context = Int(limit, "context");
        }

        var costInput = model.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object
            ? Num(cost, "input") : null;
        var costOutput = costInput.HasValue ? Num(cost, "output") : null;
        var isFree = costInput is 0 && costOutput is 0;

        var tools = true;
        var vision = false;
        if (model.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object)
        {
            if (caps.TryGetProperty("toolcall", out var tc) && tc.ValueKind == JsonValueKind.False)
            {
                tools = false;
            }

            if (caps.TryGetProperty("input", out var inputs) && inputs.ValueKind == JsonValueKind.Object
                && inputs.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.True)
            {
                vision = true;
            }
        }

        return new OpenCodeModel($"{providerId}/{modelId}", providerId, modelId, context, isFree, tools, vision);
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static double? Num(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null;

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] + "…" : text;
}
