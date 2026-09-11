// CA-A-IA · Fase 0 — Decorador de resiliencia: timeout + reintentos con backoff + clasificación.

using CaAIA.Domain.AI;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure.AI;

/// <summary>
/// Envuelve cualquier <see cref="IAIProvider"/> con timeout por intento, reintentos solo ante
/// errores reintentables (<see cref="AIProviderException.IsRetryable"/>) y backoff exponencial
/// con jitter. Nunca reintenta auth/modelo/contexto: esos fallos escalan de inmediato.
/// </summary>
public sealed class ResilientAIProvider : IAIProvider
{
    private readonly IAIProvider _inner;
    private readonly int _maxRetries;
    private readonly ILogger<ResilientAIProvider> _log;

    public ResilientAIProvider(IAIProvider inner, int maxRetries, ILogger<ResilientAIProvider> log)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxRetries = Math.Max(0, maxRetries);
        _log = log;
    }

    public string Id => _inner.Id;
    public string DisplayName => _inner.DisplayName;
    public ProviderCapabilities Capabilities => _inner.Capabilities;

    public async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeoutCts = CreateTimeoutCts(request.Timeout, cancellationToken);
            try
            {
                return await _inner.CompleteAsync(request, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (AIProviderException ex) when (ex.IsRetryable && attempt < _maxRetries)
            {
                attempt++;
                var delay = Backoff(attempt, ex.RetryAfter);
                _log.LogWarning(ex, "Provider {ProviderId} transient failure (attempt {Attempt}/{Max}). Retrying in {Delay}ms",
                    Id, attempt, _maxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public IAsyncEnumerable<AIStreamChunk> StreamAsync(AIRequest request, CancellationToken cancellationToken) =>
        // TODO(FUTURE_PHASE): reintento de streams a mitad de secuencia (reanudación por offset).
        _inner.StreamAsync(request, cancellationToken);

    public Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken cancellationToken) =>
        _inner.GetModelsAsync(cancellationToken);

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
        _inner.CheckHealthAsync(cancellationToken);

    private static CancellationTokenSource CreateTimeoutCts(TimeSpan? timeout, CancellationToken caller)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(caller);
        if (timeout.HasValue)
        {
            cts.CancelAfter(timeout.Value);
        }

        return cts;
    }

    internal static TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter.HasValue)
        {
            // Un servidor no confiable puede pedir horas (o un negativo que rompería
            // Task.Delay): acotar a [0, 60s] manteniendo el valor pedido dentro del rango.
            var clamped = retryAfter.Value < TimeSpan.Zero ? TimeSpan.Zero
                : retryAfter.Value > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60)
                : retryAfter.Value;
            return clamped;
        }

        var ms = Math.Min(30_000, 500 * Math.Pow(2, attempt - 1));
        ms += Random.Shared.Next(0, 250); // jitter
        return TimeSpan.FromMilliseconds(ms);
    }
}
