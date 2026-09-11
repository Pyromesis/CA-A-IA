// CA-A-IA — Memoria en memoria (scopes + expiración). Alternativa ligera a SqliteMemoryStore
// para tests y escenarios sin persistencia.

using System.Collections.Concurrent;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Memory;

namespace CaAIA.Infrastructure.Memory;

/// <summary>
/// <see cref="IMemoryStore"/> en memoria, thread-safe, con expiración por entrada.
/// Ver <see cref="Persistence.Sqlite.SqliteMemoryStore"/> para el backing persistente.
/// </summary>
public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly ConcurrentDictionary<Guid, MemoryEntry> _entries = new();

    public Task PutAsync(MemoryEntry entry, CancellationToken cancellationToken)
    {
        _entries[entry.Id] = entry;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryEntry>> RecallAsync(
        Guid sessionId, MemoryScope scope, string? keyPrefix, int maxEntries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<MemoryEntry> result = _entries.Values
            .Where(e => e.SessionId == sessionId && e.Scope == scope && !e.IsExpired(now))
            .Where(e => keyPrefix is null || e.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.CreatedAt)
            .Take(Math.Max(1, maxEntries))
            .ToList();
        return Task.FromResult(result);
    }

    public Task ForgetTaskAsync(Guid sessionId, Guid taskId, CancellationToken cancellationToken)
    {
        foreach (var (id, entry) in _entries.ToArray())
        {
            if (entry.SessionId == sessionId && entry.TaskId == taskId)
            {
                _entries.TryRemove(id, out _);
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> PruneExpiredAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var (id, entry) in _entries.ToArray())
        {
            if (entry.SessionId == sessionId && entry.IsExpired(now) && _entries.TryRemove(id, out _))
            {
                removed++;
            }
        }

        return Task.FromResult(removed);
    }
}
