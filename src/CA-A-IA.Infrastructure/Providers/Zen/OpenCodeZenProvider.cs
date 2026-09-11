// CA-A-IA — Adaptador OpenCode Zen: la API de modelos de OpenCode
// (https://opencode.ai/zen/v1, OpenAI-compatible, documentada en models.dev como proveedor
// "opencode" con npm @ai-sdk/openai-compatible). Key OPENCODE_API_KEY vía secret store.
// El catálogo con gratuidad/contexto/capacidades viene de models.dev (público, cacheado).

using CaAIA.Application.Configuration;
using CaAIA.Domain.AI;
using CaAIA.Domain.Security;
using CaAIA.Infrastructure.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Infrastructure.Providers.Zen;

/// <summary>Opciones de OpenCode Zen (sección <c>CaAIA:Providers:OpenCodeZen</c>).</summary>
public sealed class OpenCodeZenOptions
{
    public string BaseUrl { get; init; } = "https://opencode.ai/zen/v1";
    /// <summary>Nombre del secreto en <see cref="ISecretStore"/> (necesario para completar).</summary>
    public string ApiKeySecretName { get; init; } = "opencode-zen-api-key";
    public int DefaultTimeoutSeconds { get; init; } = 180;
}

/// <summary>OpenCode Zen → <see cref="IAIProvider"/> sobre el estándar chat completions.</summary>
public sealed class OpenCodeZenProvider : IAIProvider
{
    public const string ProviderId = "opencode-zen";

    private readonly OpenCodeZenOptions _options;
    private readonly OpenAICompatibleClient _client;
    private readonly OpenAIResponsesClient _responses;
    private readonly ModelsDevCatalog _catalog;

    public string Id => ProviderId;
    public string DisplayName => "OpenCode Zen";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Streaming | ProviderCapabilities.Tools |
        ProviderCapabilities.Vision | ProviderCapabilities.StructuredOutput |
        ProviderCapabilities.LongContext;

    /// <summary>
    /// Familias que Zen sirve por Responses API en vez de chat/completions
    /// (ver tabla de endpoints en opencode.ai/docs/zen).
    /// </summary>
    internal static bool UsesResponsesApi(string modelId) =>
        modelId.StartsWith("muse-", StringComparison.OrdinalIgnoreCase)
        || modelId.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
        || modelId.StartsWith("grok-", StringComparison.OrdinalIgnoreCase);
    // TODO(FUTURE_PHASE): clientes Messages (claude-*/qwen*) y Gemini (gemini-*) para sus
    // familias; hoy se enrutan a chat/completions y el error del servidor lo indica.

    public OpenCodeZenProvider(
        IOptions<OpenCodeZenOptions> options,
        ISecretStore secrets,
        ModelsDevCatalog catalog,
        ILogger<OpenAICompatibleClient> clientLog)
    {
        _options = options.Value;
        _catalog = catalog;
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });
        _client = new OpenAICompatibleClient(
            http, ProviderId, _options.BaseUrl,
            async ct => await secrets.RetrieveAsync(_options.ApiKeySecretName, ct).ConfigureAwait(false)
                ?? Environment.GetEnvironmentVariable("OPENCODE_ZEN_API_KEY"),
            Capabilities, clientLog);
        _responses = new OpenAIResponsesClient(
            new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            }),
            ProviderId, _options.BaseUrl,
            async ct => await secrets.RetrieveAsync(_options.ApiKeySecretName, ct).ConfigureAwait(false)
                ?? Environment.GetEnvironmentVariable("OPENCODE_ZEN_API_KEY"),
            clientLog);
    }

    public Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds);
        if (!UsesResponsesApi(request.ModelId))
        {
            return _client.CompleteAsync(request, timeout, cancellationToken);
        }

        // La Responses API v1 no recibe tools: se avisa y se envía solo el prompt.
        return _responses.CompleteAsync(WithoutTools(request), timeout, cancellationToken);
    }

    public IAsyncEnumerable<AIStreamChunk> StreamAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds);
        return UsesResponsesApi(request.ModelId)
            ? _responses.StreamAsync(WithoutTools(request), timeout, cancellationToken)
            : _client.StreamAsync(request, timeout, cancellationToken);
    }

    private static AIRequest WithoutTools(AIRequest request) => new()
    {
        ModelId = request.ModelId,
        Messages = request.Messages,
        ToolResults = request.ToolResults,
        Tools = Array.Empty<Domain.Tools.ToolDefinition>(),
        Temperature = request.Temperature,
        MaxOutputTokens = request.MaxOutputTokens,
        StructuredOutputSchema = null,
        Timeout = request.Timeout,
        Correlation = request.Correlation,
    };

    public async Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken cancellationToken)
    {
        // Catálogo enriquecido primero (gratis/contexto/capacidades); fallback al /models de Zen.
        var catalogued = await _catalog.GetOpenCodeModelsAsync(ProviderId, cancellationToken).ConfigureAwait(false);
        if (catalogued.Count > 0)
        {
            return catalogued.Select(AdjustCapabilities).ToList();
        }

        return await _client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Los modelos de Responses API no aceptan nuestras tools por esa vía: se les retiran
    /// las capacidades correspondientes para que el ejecutor no las ofrezca en vano.
    /// </summary>
    internal static AIModel AdjustCapabilities(AIModel model) =>
        UsesResponsesApi(model.Id)
            ? model with
            {
                Capabilities = model.Capabilities
                    & ~(ProviderCapabilities.Tools | ProviderCapabilities.StructuredOutput),
            }
            : model;

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await GetModelsAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AIProviderException ex) when (ex.Kind is AIErrorKind.Authentication or AIErrorKind.Authorization)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
