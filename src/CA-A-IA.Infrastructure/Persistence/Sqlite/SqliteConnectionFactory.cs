// CA-A-IA — SQLite (WAL) como persistencia local: un fichero, transaccional, concurrente.
// Esquema v1 con user_version; migración única desde JSON legacy (ver LegacyJsonImporter).

using System.Text.Json;
using CaAIA.Application.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Infrastructure.Persistence.Sqlite;

/// <summary>
/// Fábrica de conexiones a <c>{DataPath}/ca-a-ia.db</c> (WAL, foreign_keys, busy_timeout).
/// Conexiones baratas y pooled: cada operación abre la suya; escrituras serializadas.
/// </summary>
public sealed class SqliteConnectionFactory
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dbPath;
    private readonly ILogger<SqliteConnectionFactory> _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteConnectionFactory(IOptions<CaAIAOptions> options, ILogger<SqliteConnectionFactory> log)
    {
        var configured = options.Value.Persistence.DataPath;
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CA-A-IA")
            : configured;
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid persistence DataPath: '{root}'.", ex);
        }

        if (fullRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("Persistence DataPath must be a local path (no UNC).");
        }

        _dbPath = Path.Combine(fullRoot, "ca-a-ia.db");
        _log = log;
    }

    internal string DbPath => _dbPath;

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Cache = SqliteCacheMode.Shared,
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Esquema idempotente (CREATE IF NOT EXISTS): barato y seguro en cada apertura.
        await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);

        return connection;
    }

    /// <summary>Sección crítica de escritura (evita SQLITE_BUSY entre hilos del proceso).</summary>
    public async Task<T> WriteAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Reintento con backoff ante BUSY/LOCKED residual (p. ej. checkpoint +
            // bus + chat a la vez superando el busy_timeout): antes subía y se perdía.
            const int maxAttempts = 4;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var connection = await OpenAsync(ct).ConfigureAwait(false);
                    return await action(connection, ct).ConfigureAwait(false);
                }
                catch (SqliteException ex) when (IsBusy(ex) && attempt < maxAttempts)
                {
                    _log.LogDebug(ex, "SQLite BUSY (attempt {Attempt}/{Max}); retrying", attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt * attempt), ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static bool IsBusy(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY / SQLITE_LOCKED

    public async Task WriteAsync(Func<SqliteConnection, CancellationToken, Task> action, CancellationToken ct) =>
        await WriteAsync(async (c, t) => { await action(c, t).ConfigureAwait(false); return 0; }, ct)
            .ConfigureAwait(false);

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        var version = await GetUserVersionAsync(connection, ct).ConfigureAwait(false);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Database schema v{version} is newer than supported v{CurrentSchemaVersion}.");
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY, is_active INTEGER NOT NULL, data TEXT NOT NULL, updated_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS plans (
                id TEXT PRIMARY KEY, session_id TEXT NOT NULL, data TEXT NOT NULL, updated_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_plans_session ON plans(session_id);
            CREATE TABLE IF NOT EXISTS checkpoints (
                id TEXT PRIMARY KEY, session_id TEXT NOT NULL, created_utc TEXT NOT NULL, data TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_checkpoints_session ON checkpoints(session_id, created_utc);
            CREATE TABLE IF NOT EXISTS execution_logs (
                id TEXT PRIMARY KEY, session_id TEXT NOT NULL, occurred_utc TEXT NOT NULL, data TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_logs_session ON execution_logs(session_id, occurred_utc);
            CREATE TABLE IF NOT EXISTS memories (
                id TEXT PRIMARY KEY, session_id TEXT NOT NULL, scope INTEGER NOT NULL,
                task_id TEXT NULL, key TEXT NOT NULL, data TEXT NOT NULL,
                created_utc TEXT NOT NULL, expires_utc TEXT NULL);
            CREATE INDEX IF NOT EXISTS idx_memories_session ON memories(session_id, scope, created_utc);
            CREATE TABLE IF NOT EXISTS events (
                id TEXT PRIMARY KEY, session_id TEXT NULL, type INTEGER NOT NULL,
                occurred_utc TEXT NOT NULL, data TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_events_session ON events(session_id, occurred_utc);
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS chat_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NULL,
                role TEXT NOT NULL, text TEXT NOT NULL, created_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_chat_messages_order ON chat_messages(id);
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        if (version < CurrentSchemaVersion)
        {
            await SetUserVersionAsync(connection, CurrentSchemaVersion, ct).ConfigureAwait(false);
        }
    }

    private const int CurrentSchemaVersion = 1;

    private static async Task<int> GetUserVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is long l ? (int)l : 0;
    }

    private static async Task SetUserVersionAsync(SqliteConnection connection, int version, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    internal static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
