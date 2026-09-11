// CA-A-IA · Fase 0 — Fachada de memoria tipada por scopes (§17).

using CaAIA.Domain.Enums;
using CaAIA.Domain.Memory;

namespace CaAIA.Agent.Memory;

/// <summary>
/// Helpers de alto nivel sobre <see cref="IMemoryStore"/>: qué se guarda, cuándo y con qué TTL.
/// Diseñado para no contaminar el contexto: la recuperación es explícita y acotada.
/// </summary>
public sealed class AgentMemory
{
    private readonly IMemoryStore _store;

    public AgentMemory(IMemoryStore store)
    {
        _store = store;
    }

    public Task RememberDecisionAsync(Guid sessionId, string key, string content, CancellationToken ct) =>
        _store.PutAsync(new MemoryEntry(Guid.NewGuid(), MemoryScope.Decision, sessionId, null,
            key, content, DateTimeOffset.UtcNow, ExpiresAt: null), ct);

    public Task RememberErrorAsync(Guid sessionId, Guid? taskId, string key, string content, CancellationToken ct) =>
        _store.PutAsync(new MemoryEntry(Guid.NewGuid(), MemoryScope.Error, sessionId, taskId,
            key, content, DateTimeOffset.UtcNow, ExpiresAt: DateTimeOffset.UtcNow.AddDays(7)), ct);

    public Task RememberVerificationAsync(Guid sessionId, string key, string content, CancellationToken ct) =>
        _store.PutAsync(new MemoryEntry(Guid.NewGuid(), MemoryScope.Verification, sessionId, null,
            key, content, DateTimeOffset.UtcNow, ExpiresAt: null), ct);

    public Task RememberTaskNoteAsync(Guid sessionId, Guid taskId, string key, string content, CancellationToken ct) =>
        _store.PutAsync(new MemoryEntry(Guid.NewGuid(), MemoryScope.Task, sessionId, taskId,
            key, content, DateTimeOffset.UtcNow, ExpiresAt: DateTimeOffset.UtcNow.AddHours(24)), ct);

    public Task<IReadOnlyList<MemoryEntry>> RecallErrorsAsync(Guid sessionId, CancellationToken ct) =>
        _store.RecallAsync(sessionId, MemoryScope.Error, null, 20, ct);

    public Task<IReadOnlyList<MemoryEntry>> RecallDecisionsAsync(Guid sessionId, CancellationToken ct) =>
        _store.RecallAsync(sessionId, MemoryScope.Decision, null, 20, ct);
}
