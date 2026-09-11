using CaAIA.Domain.Enums;

namespace CaAIA.Domain.Memory;

/// <summary>Entrada de memoria del agente (§17) con expiración explícita.</summary>
public sealed record MemoryEntry(
    Guid Id,
    MemoryScope Scope,
    Guid SessionId,
    Guid? TaskId,
    string Key,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt.HasValue && ExpiresAt.Value <= now;
}

/// <summary>Almacén de memoria por scopes (sesión, tarea, proyecto, decisiones, errores, verificación).</summary>
public interface IMemoryStore
{
    Task PutAsync(MemoryEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryEntry>> RecallAsync(
        Guid sessionId, MemoryScope scope, string? keyPrefix, int maxEntries, CancellationToken cancellationToken);
    Task ForgetTaskAsync(Guid sessionId, Guid taskId, CancellationToken cancellationToken);
    Task<int> PruneExpiredAsync(Guid sessionId, CancellationToken cancellationToken);
}
