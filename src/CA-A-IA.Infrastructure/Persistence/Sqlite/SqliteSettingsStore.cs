// CA-A-IA — ISettingsStore en SQLite (tabla settings).

using CaAIA.Domain.Persistence;

namespace CaAIA.Infrastructure.Persistence.Sqlite;

/// <summary>Ajustes clave/valor con upsert.</summary>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly SqliteConnectionFactory _db;
    public SqliteSettingsStore(SqliteConnectionFactory db) => _db = db;

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public Task SetAsync(string key, string value, CancellationToken ct) =>
        _db.WriteAsync(async (c, t) =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO settings (key, value, updated_utc)
                VALUES ($key, $value, $updated)
                ON CONFLICT(key) DO UPDATE SET value=$value, updated_utc=$updated;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct);

    public Task RemoveAsync(string key, CancellationToken ct) =>
        _db.WriteAsync(async (c, t) =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM settings WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
            await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct);
}
