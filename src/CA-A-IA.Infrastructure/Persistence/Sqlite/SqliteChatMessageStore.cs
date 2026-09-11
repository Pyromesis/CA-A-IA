// CA-A-IA — Historial de conversación del chat en SQLite (tabla chat_messages).

using CaAIA.Domain.Persistence;

namespace CaAIA.Infrastructure.Persistence.Sqlite;

/// <summary>
/// Apéndice cronológico del chat (últimos 1000 mensajes; el ChatViewModel muestra 200).
/// Fire-and-forget desde la UI: nunca debe romper el envío de mensajes.
/// </summary>
public sealed class SqliteChatMessageStore : IChatMessageStore
{
    private const int KeepLatest = 1000;
    private const int PruneEveryAppends = 50;
    private int _appendsSincePrune;

    private readonly SqliteConnectionFactory _db;
    public SqliteChatMessageStore(SqliteConnectionFactory db) => _db = db;

    public async Task<IReadOnlyList<StoredChatMessage>> ListRecentAsync(int limit, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, role, text, created_utc FROM chat_messages
            ORDER BY id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var rows = new List<StoredChatMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var at = DateTimeOffset.TryParse(reader.GetString(4), out var parsed)
                ? parsed : DateTimeOffset.MinValue;
            rows.Add(new StoredChatMessage(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                at));
        }

        rows.Reverse();
        return rows;
    }

    public Task AppendAsync(string? sessionId, string role, string text, DateTimeOffset at, CancellationToken ct)
    {
        // La poda se limita a 1 de cada N escrituras: antes corría un DELETE O(n)
        // con subselect en CADA mensaje.
        var prune = Interlocked.Increment(ref _appendsSincePrune) >= PruneEveryAppends;
        if (prune)
        {
            Interlocked.Exchange(ref _appendsSincePrune, 0);
        }

        return _db.WriteAsync(async (c, t) =>
        {
            // Transacción síncrona (sin E/S): el using la dispone con rollback si no hay commit.
            using var tx = c.BeginTransaction();
            try
            {
                using (var cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO chat_messages (session_id, role, text, created_utc)
                        VALUES ($session, $role, $text, $at);
                        """;
                    cmd.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$role", role);
                    cmd.Parameters.AddWithValue("$text", text);
                    cmd.Parameters.AddWithValue("$at", at.ToString("O"));
                    await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
                }

                if (prune)
                {
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = """
                            DELETE FROM chat_messages WHERE id NOT IN (
                                SELECT id FROM chat_messages ORDER BY id DESC LIMIT $keep);
                            """;
                        cmd.Parameters.AddWithValue("$keep", KeepLatest);
                        await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
                    }
                }

                tx.Commit();
            }
            catch
            {
                try
                {
                    tx.Rollback();
                }
                catch (Exception)
                {
                }

                throw;
            }
        }, ct);
    }
}
