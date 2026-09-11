namespace CaAIA.Domain.AI;

/// <summary>
/// Abstracción genérica de proveedor de IA (§10). Toda la app habla contra esta interfaz;
/// OpenRouter/OpenCode/locales son adaptadores en Infrastructure. El núcleo del agente
/// NUNCA referencia SDKs concretos de proveedor.
/// </summary>
public interface IAIProvider
{
    string Id { get; }
    string DisplayName { get; }
    ProviderCapabilities Capabilities { get; }

    Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken cancellationToken);

    /// <summary>Completion con streaming. Debe respetar <paramref name="cancellationToken"/> entre chunks.</summary>
    IAsyncEnumerable<AIStreamChunk> StreamAsync(AIRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken cancellationToken);

    /// <summary>Chequeo ligero de conectividad/credenciales (para UI + watchdog).</summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
}

/// <summary>Registro extensible de proveedores (§13): añadir uno nuevo = adaptador + registro DI.</summary>
public interface IProviderRegistry
{
    void Register(IAIProvider provider);
    bool Remove(string providerId);
    IAIProvider Get(string providerId);
    IReadOnlyCollection<IAIProvider> GetAll();
    IReadOnlyCollection<AIModel> GetAvailableModels();
    Task<IReadOnlyCollection<AIModel>> GetAvailableModelsAsync(CancellationToken cancellationToken);
    IReadOnlyCollection<IAIProvider> GetProvidersSupporting(ProviderCapabilities required);
}
