// CA-A-IA — Migración única del layout JSON legacy (Fase 0) a SQLite.
// Solo corre si la BD está vacía y hay ficheros legacy; nunca borra los originales.

using System.Text.Json;
using CaAIA.Application.Configuration;
using CaAIA.Domain.Persistence;
using CaAIA.Domain.Planning;
using CaAIA.Infrastructure.Persistence.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Infrastructure.Persistence.Migration;

/// <summary>
/// Importa `sessions/`, `plans/`, `checkpoints/` y `logs/*.jsonl` a SQLite la primera vez.
/// Idempotente: si la BD ya tiene sesiones, no hace nada.
/// </summary>
public sealed class LegacyJsonImporter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly IAgentSessionStore _sessions;
    private readonly IPlanStore _plans;
    private readonly ICheckpointStore _checkpoints;
    private readonly ILogger<LegacyJsonImporter> _log;

    public LegacyJsonImporter(
        IOptions<CaAIAOptions> options,
        IAgentSessionStore sessions,
        IPlanStore plans,
        ICheckpointStore checkpoints,
        ILogger<LegacyJsonImporter> log)
    {
        var configured = options.Value.Persistence.DataPath;
        _root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CA-A-IA")
            : configured;
        _sessions = sessions;
        _plans = plans;
        _checkpoints = checkpoints;
        _log = log;
    }

    public async Task<int> MigrateOnceAsync(SqliteConnectionFactory db, CancellationToken ct)
    {
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        // ¿BD ya en uso? → nada que migrar.
        await using (var c = await db.OpenAsync(ct).ConfigureAwait(false))
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sessions;";
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0)
            {
                return 0;
            }
        }

        var migrated = 0;
        foreach (var file in SafeFiles("sessions"))
        {
            var session = Read<AgentSession>(file);
            if (session is null)
            {
                continue;
            }

            await _sessions.SaveAsync(session, ct).ConfigureAwait(false);
            migrated++;
        }

        foreach (var file in SafeFiles("plans"))
        {
            var plan = Read<Plan>(file);
            if (plan is null)
            {
                continue;
            }

            await _plans.SaveAsync(plan, ct).ConfigureAwait(false);
            migrated++;
        }

        foreach (var file in SafeFiles("checkpoints"))
        {
            var checkpoint = Read<Checkpoint>(file);
            if (checkpoint is null)
            {
                continue;
            }

            await _checkpoints.SaveCheckpointAsync(checkpoint, ct).ConfigureAwait(false);
            migrated++;
        }

        var logsDir = Path.Combine(_root, "logs");
        if (Directory.Exists(logsDir))
        {
            foreach (var file in Directory.EnumerateFiles(logsDir, "*.jsonl"))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var line in await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var entry = JsonSerializer.Deserialize<ExecutionLogEntry>(line, Json);
                        if (entry is not null)
                        {
                            await _checkpoints.AppendExecutionLogAsync(entry, ct).ConfigureAwait(false);
                            migrated++;
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
        }

        if (migrated > 0)
        {
            _log.LogInformation("Migrated {Count} legacy JSON documents to SQLite (originals preserved).", migrated);
        }

        return migrated;
    }

    private IEnumerable<string> SafeFiles(string collection)
    {
        var dir = Path.Combine(_root, collection);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json") : Enumerable.Empty<string>();
    }

    private static T? Read<T>(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return default;
        }
    }
}
