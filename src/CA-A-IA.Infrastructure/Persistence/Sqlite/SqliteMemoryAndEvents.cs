// CA-A-IA — Memoria persistente + eventos persistidos en SQLite.

using CaAIA.Domain.Enums;
using CaAIA.Domain.Events;
using CaAIA.Domain.Memory;
using CaAIA.Domain.Persistence;

namespace CaAIA.Infrastructure.Persistence.Sqlite;

/// <summary>
/// <see cref="IMemoryStore"/> con backing SQLite: la memoria de proyecto/decisiones/errores
/// sobrevive a reinicios (los TTL se siguen respetando al leer).
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore
{
    private readonly SqliteConnectionFactory _db;
    public SqliteMemoryStore(SqliteConnectionFactory db) => _db = db;

    public Task PutAsync(MemoryEntry entry, CancellationToken ct) =>
        _db.WriteAsync(async (c, t) =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO memories (id, session_id, scope, task_id, key, data, created_utc, expires_utc)
                VALUES ($id, $session, $scope, $task, $key, $data, $created, $expires)
                ON CONFLICT(id) DO UPDATE SET data=$data, expires_utc=$expires;
                """;
            cmd.Parameters.AddWithValue("$id", entry.Id.ToString("N"));
            cmd.Parameters.AddWithValue("$session", entry.SessionId.ToString("N"));
            cmd.Parameters.AddWithValue("$scope", (int)entry.Scope);
            cmd.Parameters.AddWithValue("$task", (object?)entry.TaskId?.ToString("N") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$key", entry.Key);
            cmd.Parameters.AddWithValue("$data", SqliteConnectionFactory.Serialize(entry));
            cmd.Parameters.AddWithValue("$created", entry.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$expires", (object?)entry.ExpiresAt?.ToString("O") ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct);

    public async Task<IReadOnlyList<MemoryEntry>> RecallAsync(
        Guid sessionId, MemoryScope scope, string? keyPrefix, int maxEntries, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT data FROM memories
            WHERE session_id = $s AND scope = $scope
            ORDER BY created_utc DESC LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId.ToString("N"));
        cmd.Parameters.AddWithValue("$scope", (int)scope);
        cmd.Parameters.AddWithValue("$max", Math.Clamp(maxEntries, 1, 1000));
        var now = DateTimeOffset.UtcNow;
        var result = new List<MemoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var entry = SqliteConnectionFactory.Deserialize<MemoryEntry>(reader.GetString(0));
            if (entry is null || entry.IsExpired(now))
            {
                continue;
            }

            if (keyPrefix is not null && !entry.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    public Task ForgetTaskAsync(Guid sessionId, Guid taskId, CancellationToken ct) =>
        _db.WriteAsync(async (c, t) =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM memories WHERE session_id = $s AND task_id = $task;";
            cmd.Parameters.AddWithValue("$s", sessionId.ToString("N"));
            cmd.Parameters.AddWithValue("$task", taskId.ToString("N"));
            await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct);

    public Task<int> PruneExpiredAsync(Guid sessionId, CancellationToken ct) =>
        _db.WriteAsync(async (c, t) =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM memories WHERE session_id = $s AND expires_utc IS NOT NULL AND expires_utc <= $now;";
            cmd.Parameters.AddWithValue("$s", sessionId.ToString("N"));
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            return await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct);
}

/// <summary><see cref="IEventStore"/> en SQLite: historial consultable por sesión (replay).</summary>
public sealed class SqliteEventStore : IEventStore
{
    private readonly SqliteConnectionFactory _db;
    public SqliteEventStore(SqliteConnectionFactory db) => _db = db;

    public Task AppendAsync(AgentEvent @event, CancellationToken ct) =>
        _db.WriteAsync(async (c, t) =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO events (id, session_id, type, occurred_utc, data)
                VALUES ($id, $session, $type, $occurred, $data)
                ON CONFLICT(id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$id", @event.Id.ToString("N"));
            cmd.Parameters.AddWithValue("$session",
                (object?)@event.Correlation?.SessionId.ToString("N") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$type", (int)@event.Type);
            cmd.Parameters.AddWithValue("$occurred", @event.OccurredAt.ToString("O"));
            cmd.Parameters.AddWithValue("$data", SqliteConnectionFactory.Serialize(@event));
            await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct);

    public async Task<IReadOnlyList<AgentEvent>> ReadAsync(Guid? sessionId, int maxEntries, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        if (sessionId.HasValue)
        {
            cmd.CommandText = "SELECT data FROM events WHERE session_id = $s ORDER BY occurred_utc DESC LIMIT $max;";
            cmd.Parameters.AddWithValue("$s", sessionId.Value.ToString("N"));
        }
        else
        {
            cmd.CommandText = "SELECT data FROM events ORDER BY occurred_utc DESC LIMIT $max;";
        }

        cmd.Parameters.AddWithValue("$max", Math.Clamp(maxEntries, 1, 1000));
        var result = new List<AgentEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var @event = SqliteConnectionFactory.Deserialize<AgentEvent>(reader.GetString(0));
            if (@event is not null)
            {
                result.Add(@event);
            }
        }

        result.Reverse(); // cronológico para replay
        return result;
    }
}
