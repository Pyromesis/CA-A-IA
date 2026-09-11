// CA-A-IA — Adaptador OpenRouter (§11): configuración + delegación al cliente OpenAI-compatible.
// Sin SDKs del proveedor; la key sale de ISecretStore, nunca de ficheros.

using CaAIA.Domain.AI;
using CaAIA.Domain.Security;
using CaAIA.Infrastructure.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Infrastructure.Providers.OpenRouter;

/// <summary>Opciones del adaptador OpenRouter (sección <c>CaAIA:Providers:OpenRouter</c>).</summary>
public sealed class OpenRouterOptions
{
    /// <summary>Base URL configurable; valor = endpoint público documentado de OpenRouter.</summary>
    public string BaseUrl { get; init; } = "https://openrouter.ai/api/v1";
    /// <summary>Nombre del secreto en <see cref="ISecretStore"/> (nunca la key en claro).</summary>
    public string ApiKeySecretName { get; init; } = "openrouter-api-key";
    public int DefaultTimeoutSeconds { get; init; } = 120;
}

/// <summary>Adaptador OpenRouter → <see cref="IAIProvider"/> sobre el estándar chat completions.</summary>
public sealed class OpenRouterProvider : IAIProvider
{
    public const string ProviderId = "openrouter";

    private readonly OpenAICompatibleClient _client;
    private readonly OpenRouterOptions _options;

    public string Id => ProviderId;
    public string DisplayName => "OpenRouter";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Streaming | ProviderCapabilities.Tools |
        ProviderCapabilities.Vision | ProviderCapabilities.StructuredOutput |
        ProviderCapabilities.LongContext;

    public OpenRouterProvider(
        IOptions<OpenRouterOptions> options,
        ISecretStore secrets,
        ILogger<OpenAICompatibleClient> clientLog)
    {
        _options = options.Value;
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });
        _client = new OpenAICompatibleClient(
            http, ProviderId, _options.BaseUrl,
            async ct => await secrets.RetrieveAsync(_options.ApiKeySecretName, ct).ConfigureAwait(false)
                ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
            Capabilities, clientLog,
            sendReasoningEffort: true); // verificado: reasoning.effort en openrouter.ai/docs
    }

    public Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken cancellationToken) =>
        _client.CompleteAsync(request, TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds), cancellationToken);

    public IAsyncEnumerable<AIStreamChunk> StreamAsync(AIRequest request, CancellationToken cancellationToken) =>
        _client.StreamAsync(request, TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds), cancellationToken);

    public Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken cancellationToken) =>
        _client.GetModelsAsync(cancellationToken);

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await GetModelsAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AIProviderException ex) when (ex.Kind is AIErrorKind.Authentication or AIErrorKind.Authorization)
        {
            throw; // credenciales mal: que lo vea el usuario, no un "false" mudo
        }
        catch (Exception)
        {
            return false;
        }
    }
}
