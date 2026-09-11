// CA-A-IA · Fase 0 — Registro extensible de proveedores (§13).

using System.Collections.Concurrent;
using CaAIA.Domain.AI;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure.AI;

/// <summary>
/// Implementación thread-safe de <see cref="IProviderRegistry"/>.
/// Nuevo proveedor = adaptador <see cref="IAIProvider"/> + una línea en DI. Sin tocar el núcleo.
/// </summary>
public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly ConcurrentDictionary<string, IAIProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ProviderRegistry> _log;

    public ProviderRegistry(ILogger<ProviderRegistry> log)
    {
        _log = log;
    }

    public void Register(IAIProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!_providers.TryAdd(provider.Id, provider))
        {
            throw new InvalidOperationException($"Provider '{provider.Id}' is already registered.");
        }

        _log.LogInformation("AI provider registered: {ProviderId} ({Name})", provider.Id, provider.DisplayName);
    }

    public bool Remove(string providerId) => _providers.TryRemove(providerId, out _);

    public IAIProvider Get(string providerId)
    {
        if (_providers.TryGetValue(providerId, out var provider))
        {
            return provider;
        }

        throw new KeyNotFoundException(
            $"AI provider '{providerId}' is not registered. Registered: {string.Join(", ", _providers.Keys)}.");
    }

    public IReadOnlyCollection<IAIProvider> GetAll() => _providers.Values.ToList();

    public IReadOnlyCollection<AIModel> GetAvailableModels() =>
        // Compat: agregación síncrona legacy. El código nuevo debe usar la variante async.
        GetAvailableModelsAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task<IReadOnlyCollection<AIModel>> GetAvailableModelsAsync(CancellationToken cancellationToken)
    {
        var tasks = _providers.Values.Select(p => GetModelsSafeAsync(p, cancellationToken)).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(m => m).ToList();
    }

    public IReadOnlyCollection<IAIProvider> GetProvidersSupporting(ProviderCapabilities required) =>
        _providers.Values.Where(p => (p.Capabilities & required) == required).ToList();

    private async Task<IEnumerable<AIModel>> GetModelsSafeAsync(IAIProvider provider, CancellationToken ct)
    {
        try
        {
            return await provider.GetModelsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Provider {ProviderId} failed to list models", provider.Id);
            return Enumerable.Empty<AIModel>();
        }
    }

    private IEnumerable<AIModel> GetModelsSafe(IAIProvider provider)
    {
        try
        {
            // Síncrono por simplicidad de Fase 0; la UI usa GetModelsAsync con await.
            return provider.GetModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Provider {ProviderId} failed to list models", provider.Id);
            return Enumerable.Empty<AIModel>();
        }
    }
}
