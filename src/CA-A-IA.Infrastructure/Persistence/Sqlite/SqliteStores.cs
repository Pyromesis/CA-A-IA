// CA-A-IA — Stores SQLite tras las interfaces de Domain (reemplazan a los ficheros JSON).

using CaAIA.Domain.Persistence;
using CaAIA.Domain.Planning;
using Microsoft.Data.Sqlite;

namespace CaAIA.Infrastructure.Persistence.Sqlite;

public sealed class SqlitePlanStore : IPlanStore
{
    private readonly SqliteConnectionFactory _db;
    public SqlitePlanStore(SqliteConnectionFactory db) => _db = db;

    public Task SaveAsync(Plan plan, CancellationToken ct) =>
        _db.WriteAsync(async (c, t) =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO plans (id, session_id, data, updated_utc)
                VALUES ($id, $session, $data, $updated)
                ON CONFLICT(id) DO UPDATE SET session_id=$session, data=$data, updated_utc=$updated;
                """;
            cmd.Parameters.AddWithValue("$id", plan.Id.ToString("N"));
            cmd.Parameters.AddWithValue("$session", plan.SessionId.ToString("N"));
            cmd.Parameters.AddWithValue("$data", SqliteConnectionFactory.Serialize(plan));
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct);

    public async Task<Plan?> LoadAsync(Guid planId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM plans WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", planId.ToString("N"));
        var data = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return data is null ? null : SqliteConnectionFactory.Deserialize<Plan>(data);
    }

    public async Task<IReadOnlyList<Plan>> ListBySessionAsync(Guid sessionId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM plans WHERE session_id = $s;";
        cmd.Parameters.AddWithValue("$s", sessionId.ToString("N"));
        var result = new List<Plan>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var plan = SqliteConnectionFactory.Deserialize<Plan>(reader.GetString(0));
            if (plan is not null)
            {
                result.Add(plan);
            }
        }

        return result;
    }
}

public sealed class SqliteAgentSessionStore : IAgentSessionStore
{
    private readonly SqliteConnectionFactory _db;
    public SqliteAgentSessionStore(SqliteConnectionFactory db) => _db = db;

    public Task SaveAsync(AgentSession session, CancellationToken ct) =>
        _db.WriteAsync(async (c, t) =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (id, is_active, data, updated_utc)
                VALUES ($id, $active, $data, $updated)
                ON CONFLICT(id) DO UPDATE SET is_active=$active, data=$data, updated_utc=$updated;
                """;
            cmd.Parameters.AddWithValue("$id", session.Id.ToString("N"));
            cmd.Parameters.AddWithValue("$active", session.IsActive ? 1 : 0);
            cmd.Parameters.AddWithValue("$data", SqliteConnectionFactory.Serialize(session));
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct);

    public async Task<AgentSession?> LoadAsync(Guid sessionId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM sessions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", sessionId.ToString("N"));
        var data = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return data is null ? null : SqliteConnectionFactory.Deserialize<AgentSession>(data);
    }

    public async Task<IReadOnlyList<AgentSession>> ListActiveAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM sessions WHERE is_active = 1;";
        var result = new List<AgentSession>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var session = SqliteConnectionFactory.Deserialize<AgentSession>(reader.GetString(0));
            if (session is not null && session.IsActive)
            {
                result.Add(session);
            }
        }

        return result;
    }

    public Task DeleteAsync(Guid sessionId, CancellationToken ct) =>
        _db.WriteAsync(async (c, t) =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", sessionId.ToString("N"));
            await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct);
}

public sealed class SqliteCheckpointStore : ICheckpointStore
{
    private readonly SqliteConnectionFactory _db;
    public SqliteCheckpointStore(SqliteConnectionFactory db) => _db = db;

    public Task SaveCheckpointAsync(Checkpoint checkpoint, CancellationToken ct) =>
        _db.WriteAsync(async (c, t) =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO checkpoints (id, session_id, created_utc, data)
                VALUES ($id, $session, $created, $data)
                ON CONFLICT(id) DO UPDATE SET data=$data;
                """;
            cmd.Parameters.AddWithValue("$id", checkpoint.Id.ToString("N"));
            cmd.Parameters.AddWithValue("$session", checkpoint.SessionId.ToString("N"));
            cmd.Parameters.AddWithValue("$created", checkpoint.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$data", SqliteConnectionFactory.Serialize(checkpoint));
            await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct);

    public async Task<Checkpoint?> LoadLatestAsync(Guid sessionId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM checkpoints WHERE session_id = $s ORDER BY created_utc DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$s", sessionId.ToString("N"));
        var data = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return data is null ? null : SqliteConnectionFactory.Deserialize<Checkpoint>(data);
    }

    public async Task<IReadOnlyList<Checkpoint>> ListBySessionAsync(Guid sessionId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM checkpoints WHERE session_id = $s ORDER BY created_utc;";
        cmd.Parameters.AddWithValue("$s", sessionId.ToString("N"));
        var result = new List<Checkpoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var checkpoint = SqliteConnectionFactory.Deserialize<Checkpoint>(reader.GetString(0));
            if (checkpoint is not null)
            {
                result.Add(checkpoint);
            }
        }

        return result;
    }

    public Task AppendExecutionLogAsync(ExecutionLogEntry entry, CancellationToken ct) =>
        _db.WriteAsync(async (c, t) =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO execution_logs (id, session_id, occurred_utc, data)
                VALUES ($id, $session, $occurred, $data)
                ON CONFLICT(id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$id", entry.Id.ToString("N"));
            cmd.Parameters.AddWithValue("$session", entry.SessionId.ToString("N"));
            cmd.Parameters.AddWithValue("$occurred", entry.OccurredAt.ToString("O"));
            cmd.Parameters.AddWithValue("$data", SqliteConnectionFactory.Serialize(entry));
            await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct);

    public async Task<IReadOnlyList<ExecutionLogEntry>> ReadExecutionLogAsync(Guid sessionId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM execution_logs WHERE session_id = $s ORDER BY occurred_utc;";
        cmd.Parameters.AddWithValue("$s", sessionId.ToString("N"));
        var result = new List<ExecutionLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var entry = SqliteConnectionFactory.Deserialize<ExecutionLogEntry>(reader.GetString(0));
            if (entry is not null)
            {
                result.Add(entry);
            }
        }

        return result;
    }
}
