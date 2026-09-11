// CA-A-IA — Adaptador para modelos locales vía API OpenAI-compatible (/v1).
// Sin key por defecto; deshabilitado hasta configurarse.

using CaAIA.Domain.AI;
using CaAIA.Infrastructure.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Infrastructure.Providers.Local;

/// <summary>Opciones del adaptador local (sección <c>CaAIA:Providers:Local</c>).</summary>
public sealed class LocalModelOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>Base URL fija (si se conoce). Vacía = autodetección.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Candidatos OpenAI-compatibles a probar en orden (Ollama, LM Studio).</summary>
    public IReadOnlyList<string> Candidates { get; init; } =
        new[] { "http://localhost:11434/v1", "http://localhost:1234/v1" };

    public int DefaultTimeoutSeconds { get; init; } = 300;
    public int ProbeTimeoutSeconds { get; init; } = 3;
}

/// <summary>
/// Adaptador local → <see cref="IAIProvider"/> sobre el estándar chat completions.
/// Autodetecta Ollama/LM Studio en local: sin cuentas ni keys.
/// </summary>
public sealed class LocalModelProvider : IAIProvider
{
    public const string ProviderId = "local";

    private readonly LocalModelOptions _options;
    private readonly ILogger<OpenAICompatibleClient> _clientLog;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OpenAICompatibleClient? _client;
    private string? _activeBaseUrl;

    public string Id => ProviderId;
    public string DisplayName => "Local models";
    public ProviderCapabilities Capabilities => ProviderCapabilities.Streaming | ProviderCapabilities.Tools;

    public LocalModelProvider(IOptions<LocalModelOptions> options, ILogger<OpenAICompatibleClient> clientLog)
    {
        _options = options.Value;
        _clientLog = clientLog;
    }

    private void RequireEnabled()
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException(
                "Local provider is disabled. Set CaAIA:Providers:Local:Enabled=true.");
        }
    }

    /// <summary>Resuelve (y cachea) el endpoint: fijo, o el primer candidato con vida.</summary>
    private async Task<OpenAICompatibleClient> ClientAsync(CancellationToken ct)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var urls = string.IsNullOrWhiteSpace(_options.BaseUrl)
                ? _options.Candidates
                : new[] { _options.BaseUrl };
            using var probe = CreateHttp();
            var found = await ProbeFirstAsync(probe, urls,
                TimeSpan.FromSeconds(Math.Clamp(_options.ProbeTimeoutSeconds, 1, 15)), ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "No local model server found. Install Ollama (winget install Ollama.Ollama), " +
                    "run 'ollama pull qwen3:8b' and 'ollama serve', or start LM Studio with its server on. " +
                    "Then reload models.");
            _activeBaseUrl = found;
            _client = new OpenAICompatibleClient(CreateHttp(), ProviderId, found,
                _ => Task.FromResult<string?>(null), Capabilities, _clientLog);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Devuelve la primera base URL que responde a GET /models (testeable).</summary>
    internal static async Task<string?> ProbeFirstAsync(
        HttpClient http, IEnumerable<string> candidates, TimeSpan timeout, CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var url = candidate.TrimEnd('/') + "/models";
            try
            {
                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                using var response = await http.GetAsync(url, linked.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException
                || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // Sin red local, timeout o respuesta mala: siguiente candidato.
                // La cancelación del llamador (ct) sí se propaga.
            }
        }

        return null;
    }

    private static HttpClient CreateHttp() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

    public async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken cancellationToken)
    {
        RequireEnabled();
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.CompleteAsync(request, TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds), cancellationToken)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<AIStreamChunk> StreamAsync(
        AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RequireEnabled();
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var chunk in client.StreamAsync(
            request, TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds), cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public async Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Array.Empty<AIModel>();
        }

        try
        {
            var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
            var models = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            return models.Select(m => m with { IsFree = true }).ToList();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<AIModel>();
        }
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return false;
        }

        try
        {
            var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
            _ = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
