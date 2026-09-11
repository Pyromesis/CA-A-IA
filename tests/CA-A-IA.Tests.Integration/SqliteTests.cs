// CA-A-IA — Tests SQLite: eventos (persist + replay), memoria persistente y migración legacy.

using System.Text.Json;
using CaAIA.Application.Configuration;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Events;
using CaAIA.Domain.Memory;
using CaAIA.Domain.Persistence;
using CaAIA.Domain.Planning;
using CaAIA.Infrastructure.Events;
using CaAIA.Infrastructure.Persistence.Migration;
using CaAIA.Infrastructure.Persistence.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaAIA.Tests.Integration;

public sealed class SqliteTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"ca-a-ia-sql-{Guid.NewGuid():N}");
    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch (Exception) { }
    }

    private SqliteConnectionFactory Factory() => new(
        Options.Create(new CaAIAOptions { Persistence = new PersistenceSettings { DataPath = _dataPath } }),
        NullLogger<SqliteConnectionFactory>.Instance);

    private static AgentEvent Event(Guid session, AgentEventType type, string summary) =>
        AgentEvent.Create(type, new Domain.Correlation.CorrelationContext(Guid.NewGuid(), session, Guid.NewGuid()), summary);

    [Fact]
    public async Task Events_PersistAndReplay_InOrder()
    {
        var store = new SqliteEventStore(Factory());
        var session = Guid.NewGuid();
        await store.AppendAsync(Event(session, AgentEventType.TaskStarted, "first"), CancellationToken.None);
        await store.AppendAsync(Event(session, AgentEventType.TaskCompleted, "second"), CancellationToken.None);
        await store.AppendAsync(Event(Guid.NewGuid(), AgentEventType.TaskStarted, "other"), CancellationToken.None);

        var replay = await store.ReadAsync(session, 10, CancellationToken.None);
        Assert.Equal(2, replay.Count);
        Assert.Equal("first", replay[0].Summary);
        Assert.Equal("second", replay[1].Summary);
    }

    [Fact]
    public async Task ChatHistory_AppendsAndLists_InOrder()
    {
        var store = new SqliteChatMessageStore(Factory());
        Assert.Empty(await store.ListRecentAsync(10, CancellationToken.None));

        var session = Guid.NewGuid().ToString("N");
        await store.AppendAsync(null, "system", "bienvenida", DateTimeOffset.UtcNow, CancellationToken.None);
        await store.AppendAsync(session, "user", "hola", DateTimeOffset.UtcNow, CancellationToken.None);
        await store.AppendAsync(session, "agent", "buenas", DateTimeOffset.UtcNow, CancellationToken.None);

        var recent = await store.ListRecentAsync(10, CancellationToken.None);
        Assert.Equal(3, recent.Count);
        Assert.Equal("system", recent[0].Role);
        Assert.Equal("bienvenida", recent[0].Text);
        Assert.Null(recent[0].SessionId);
        Assert.Equal("user", recent[1].Role);
        Assert.Equal(session, recent[1].SessionId);
        Assert.Equal("agent", recent[2].Role);

        var last2 = await store.ListRecentAsync(2, CancellationToken.None);
        Assert.Equal(2, last2.Count);
        Assert.Equal("buenas", last2[1].Text);
    }

    [Fact]
    public async Task RecordingBus_PersistsSessionEvents_Flushable()
    {
        var inner = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var bus = new RecordingEventBus(inner, new SqliteEventStore(Factory()),
            NullLogger<RecordingEventBus>.Instance);
        var session = Guid.NewGuid();

        var received = new List<AgentEvent>();
        using var _ = bus.SubscribeAll((e, _) => { received.Add(e); return Task.CompletedTask; });
        bus.Publish(Event(session, AgentEventType.PlanCreated, "plan!"));
        bus.Publish(AgentEvent.Create(AgentEventType.AgentStarted, null, "global, no session"));
        await bus.FlushAsync();
        await bus.DisposeAsync();

        Assert.Equal(2, received.Count); // el canal en caliente lo ve todo
        var stored = new SqliteEventStore(Factory());
        var replay = await stored.ReadAsync(session, 10, CancellationToken.None);
        Assert.Single(replay); // solo el de sesión persiste
    }

    [Fact]
    public async Task Memory_SurvivesFactoryRebuild_RespectsExpiry()
    {
        var session = Guid.NewGuid();
        var store = new SqliteMemoryStore(Factory());
        await store.PutAsync(new MemoryEntry(Guid.NewGuid(), MemoryScope.Decision, session, null,
            "stack", "xunit", DateTimeOffset.UtcNow, null), CancellationToken.None);
        await store.PutAsync(new MemoryEntry(Guid.NewGuid(), MemoryScope.Error, session, null,
            "stale", "old", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1)),
            CancellationToken.None);

        var store2 = new SqliteMemoryStore(Factory()); // "reinicio": nueva factoría, misma BD
        var decisions = await store2.RecallAsync(session, MemoryScope.Decision, null, 10, CancellationToken.None);
        Assert.Single(decisions);
        var errors = await store2.RecallAsync(session, MemoryScope.Error, null, 10, CancellationToken.None);
        Assert.Empty(errors);
        Assert.Equal(1, await store2.PruneExpiredAsync(session, CancellationToken.None));
    }

    [Fact]
    public async Task LegacyMigration_ImportsOnce_ThenNoops()
    {
        var session = new AgentSession { WorkspacePath = _dataPath, UserRequest = "legacy" };
        var plan = new Plan { SessionId = session.Id, Goal = "g", Tasks = new List<AgentTask>() };
        session.AttachPlan(plan.Id);
        var checkpoint = new Checkpoint(Guid.NewGuid(), session.Id, plan.Id,
            AgentState.Idle, null, 1, DateTimeOffset.UtcNow, "n");
        WriteLegacy("sessions", session.Id.ToString("N"), session);
        WriteLegacy("plans", plan.Id.ToString("N"), plan);
        WriteLegacy("checkpoints", checkpoint.Id.ToString("N"), checkpoint);
        var logsDir = Path.Combine(_dataPath, "logs");
        Directory.CreateDirectory(logsDir);
        await File.WriteAllTextAsync(Path.Combine(logsDir, $"{session.Id:N}.jsonl"),
            JsonSerializer.Serialize(new ExecutionLogEntry(Guid.NewGuid(), session.Id,
                DateTimeOffset.UtcNow, "Session", "created.")) + "\n");

        var options = Options.Create(new CaAIAOptions
        {
            Persistence = new PersistenceSettings { DataPath = _dataPath },
        });
        var db = Factory();
        var importer = new LegacyJsonImporter(options,
            new SqliteAgentSessionStore(db), new SqlitePlanStore(db), new SqliteCheckpointStore(db),
            NullLogger<LegacyJsonImporter>.Instance);

        var migrated = await importer.MigrateOnceAsync(db, CancellationToken.None);
        Assert.Equal(4, migrated);

        var recovered = await new SqliteAgentSessionStore(db).LoadAsync(session.Id, CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal("legacy", recovered.UserRequest);
        var log = await new SqliteCheckpointStore(db).ReadExecutionLogAsync(session.Id, CancellationToken.None);
        Assert.Single(log);

        Assert.Equal(0, await importer.MigrateOnceAsync(db, CancellationToken.None)); // idempotente
    }

    private void WriteLegacy<T>(string collection, string id, T document)
    {
        var dir = Path.Combine(_dataPath, collection);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{id}.json"), JsonSerializer.Serialize(document));
    }
}
