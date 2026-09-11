// CA-A-IA — Cliente mínimo de la Responses API de OpenAI (la que Zen usa para las
// familias muse-*, gpt-* y grok-*, según opencode.ai/docs/zen). Sin tools ni streaming:
// emite la respuesta completa (mismo patrón documentado que el fallback de OpenCode serve).

using System.Net;
using System.Text;
using System.Text.Json;
using CaAIA.Domain.AI;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure.AI;

/// <summary>
/// POST {base}/responses {model, input}. Respuesta: output[] con items message/partes
/// output_text + usage {input_tokens, output_tokens}. Errores → <see cref="AIProviderException"/>.
/// </summary>
public sealed class OpenAIResponsesClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _providerId;
    private readonly Func<CancellationToken, Task<string?>> _apiKeyProvider;
    private readonly ILogger _log;

    public OpenAIResponsesClient(
        HttpClient http,
        string providerId,
        string baseUrl,
        Func<CancellationToken, Task<string?>> apiKeyProvider,
        ILogger log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _providerId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        _baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).TrimEnd('/');
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<AIResponse> CompleteAsync(AIRequest request, TimeSpan defaultTimeout, CancellationToken ct)
    {
        var input = new StringBuilder();
        foreach (var message in request.Messages)
        {
            input.Append(message.Role switch
            {
                AIRole.System => "[system]\n",
                AIRole.Assistant => "[assistant]\n",
                AIRole.Tool => "[tool]\n",
                _ => "",
            });
            input.AppendLine(message.Content);
            foreach (var call in message.ToolCalls)
            {
                input.AppendLine($"[tool call {call.ToolName}: {call.ArgumentsJson}]");
            }
        }

        foreach (var result in request.ToolResults)
        {
            input.AppendLine($"[tool result {result.ToolName}: {result.Content}]");
        }

        using var body = new StringContent(
            JsonSerializer.Serialize(new { model = request.ModelId, input = input.ToString() }),
            Encoding.UTF8, "application/json");
        using var timeoutCts = new CancellationTokenSource(request.Timeout ?? defaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        HttpResponseMessage response;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/responses") { Content = body };
            var key = await _apiKeyProvider(linked.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(key))
            {
                req.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            }

            req.Headers.UserAgent.ParseAdd("CA-A-IA/0.2");
            response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AIProviderException(_providerId, AIErrorKind.Timeout,
                "Request timed out.", isRetryable: true);
        }
        catch (HttpRequestException ex)
        {
            throw new AIProviderException(_providerId, AIErrorKind.Network,
                $"Network error: {ex.Message}", isRetryable: true, inner: ex);
        }

        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            JsonDocument doc;
            try
            {
                doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new AIProviderException(_providerId, AIErrorKind.ProviderInternal,
                    $"Invalid JSON from provider (HTTP {(int)response.StatusCode}).",
                    isRetryable: true, inner: ex);
            }

            using (doc)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw MapError(doc, response.StatusCode);
                }

                return Parse(doc, request.ModelId);
            }
        }
    }

    public async IAsyncEnumerable<AIStreamChunk> StreamAsync(
        AIRequest request, TimeSpan defaultTimeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Sin streaming en v1: respuesta completa como único delta (documentado).
        var response = await CompleteAsync(request, defaultTimeout, ct).ConfigureAwait(false);
        if (response.Content.Length > 0)
        {
            yield return new AIStreamChunk(response.Content);
        }

        yield return new AIStreamChunk(string.Empty, IsFinal: true);
    }

    private AIProviderException MapError(JsonDocument doc, HttpStatusCode status)
    {
        var message = "Provider error";
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
            {
                message = error.GetString() ?? message;
            }
            else if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                message = m.GetString() ?? message;
            }
        }

        var kind = status switch
        {
            HttpStatusCode.Unauthorized => AIErrorKind.Authentication,
            HttpStatusCode.Forbidden => AIErrorKind.Authorization,
            HttpStatusCode.NotFound => AIErrorKind.ModelNotFound,
            HttpStatusCode.TooManyRequests => AIErrorKind.RateLimited,
            >= HttpStatusCode.InternalServerError => AIErrorKind.ProviderInternal,
            _ => AIErrorKind.ProviderInternal,
        };
        return new AIProviderException(_providerId, kind, message,
            isRetryable: kind is AIErrorKind.RateLimited or AIErrorKind.ProviderInternal);
    }

    internal static AIResponse Parse(JsonDocument doc, string modelId)
    {
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(text.GetString());
                    }
                }
            }
        }

        var usage = new TokenUsage(0, 0);
        if (doc.RootElement.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            usage = new TokenUsage(
                u.TryGetProperty("input_tokens", out var p) && p.TryGetInt32(out var pi) ? pi : 0,
                u.TryGetProperty("output_tokens", out var t) && t.TryGetInt32(out var to) ? to : 0);
        }

        return new AIResponse(sb.ToString(), Array.Empty<AIToolCall>(), modelId, usage, "stop");
    }
}
