// CA-A-IA — Tests del LlmTaskExecutor con proveedor guionizado + herramientas reales.

using CaAIA.Agent.Execution;
using CaAIA.Application.Configuration;
using CaAIA.Domain.AI;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Events;
using CaAIA.Domain.Interaction;
using CaAIA.Domain.Memory;
using CaAIA.Domain.Persistence;
using CaAIA.Domain.Planning;
using CaAIA.Domain.Security;
using CaAIA.Domain.Tools;
using CaAIA.Infrastructure.Events;
using CaAIA.Infrastructure.FileSystem;
using CaAIA.Infrastructure.Context;
using CaAIA.Infrastructure.Memory;
using CaAIA.Infrastructure.Persistence.Sqlite;
using CaAIA.Infrastructure.Security;
using CaAIA.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaAIA.Tests.Integration;

public sealed class LlmExecutorTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"ca-a-ia-llm-{Guid.NewGuid():N}");
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"ca-a-ia-ws-{Guid.NewGuid():N}");

    public LlmExecutorTests()
    {
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch (Exception) { }
        try { Directory.Delete(_workspace, recursive: true); } catch (Exception) { }
    }

    private sealed record Harness(
        LlmTaskExecutor Executor, ScriptedLlmProvider Provider, IAgentSessionStore Sessions);

    private Harness Create(IUserConfirmation confirmation, bool confirmWrites, IEventBus? events = null)
    {
        var dataOptions = Options.Create(new CaAIAOptions
        {
            Persistence = new PersistenceSettings { DataPath = _dataPath },
        });
        var db = new SqliteConnectionFactory(dataOptions, NullLogger<SqliteConnectionFactory>.Instance);
        var sessions = new SqliteAgentSessionStore(db);

        var providers = new Infrastructure.AI.ProviderRegistry(NullLogger<Infrastructure.AI.ProviderRegistry>.Instance);
        var provider = new ScriptedLlmProvider();
        providers.Register(provider);

        var tools = new ToolRegistry();
        tools.Register(new ReadFileTool());
        tools.Register(new ListDirectoryTool());
        tools.Register(new WriteFileTool());

        var options = Options.Create(new CaAIAOptions
        {
            Providers = new ProviderSettings
            {
                DefaultProviderId = "fake-llm", DefaultModelId = "m", RequestTimeoutSeconds = 30,
            },
            Agent = new AgentSettings { MaxToolIterations = 5, MaxTaskContextChars = 20_000 },
            Execution = new ExecutionSettings { ToolTimeoutSeconds = 30 },
            Security = new SecuritySettings
            {
                RequireConfirmationForWrite = confirmWrites,
                RequireConfirmationForExecute = false,
            },
        });

        var prefs = new Application.Services.UserPreferences(options,
            new MemorySettingsStore(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Application.Services.UserPreferences>.Instance);
        // El nivel de autorización manda sobre el flag legacy: sin confirmación = Nivel 2.
        prefs.SetAuthorizationLevel(confirmWrites
            ? CaAIA.Domain.Security.AuthorizationLevel.ConfirmChanges
            : CaAIA.Domain.Security.AuthorizationLevel.EditWithoutPcControl);

        var executor = new LlmTaskExecutor(
            sessions, providers, prefs,
            tools,
            new ToolPermissionService(),
            new WorkspaceContextBuilder(new WorkspaceReader()),
            confirmation, new InMemoryMemoryStore(),
            events ?? new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance),
            options, NullLogger<LlmTaskExecutor>.Instance);
        return new Harness(executor, provider, sessions);
    }

    private async Task<(Plan Plan, AgentTask Task)> SeedAsync(IAgentSessionStore sessions, string title)
    {
        var session = new AgentSession { WorkspacePath = _workspace, UserRequest = title };
        await sessions.SaveAsync(session, CancellationToken.None);
        var task = new AgentTask { Title = title, Description = title };
        var plan = new Plan { SessionId = session.Id, Goal = title, Tasks = new List<AgentTask> { task } };
        plan.SetQuestions(Array.Empty<Clarification>());
        plan.MarkInReview();
        plan.MarkApproved();
        plan.MarkExecuting();
        // El plan necesita persistirse: store SQLite sobre el mismo DataPath.
        var plans = new SqlitePlanStore(
            new SqliteConnectionFactory(
                Options.Create(new CaAIAOptions { Persistence = new PersistenceSettings { DataPath = _dataPath } }),
                NullLogger<SqliteConnectionFactory>.Instance));
        await plans.SaveAsync(plan, CancellationToken.None);
        session.AttachPlan(plan.Id);
        await sessions.SaveAsync(session, CancellationToken.None);
        return (plan, task);
    }

    [Fact]
    public async Task ReadFlow_ToolResultFeedsNextIteration_ThenSucceeds()
    {
        var note = Path.Combine(_workspace, "note.txt");
        await File.WriteAllTextAsync(note, "secret-content");
        var h = Create(new AllowConfirmation(), confirmWrites: false);
        var (plan, task) = await SeedAsync(h.Sessions, "Read the note");

        h.Provider.Enqueue(req => new AIResponse("I'll read it.",
            new[] { new AIToolCall("c1", "ReadFile", $"{{\"path\":{Json(note)}}}") },
            "m", new TokenUsage(1, 1)));
        h.Provider.Enqueue(req =>
        {
            var toolMsg = req.Messages.Last(m => m.Role == AIRole.Tool);
            Assert.Contains("secret-content", toolMsg.Content);
            return new AIResponse("Done: the note says secret-content.",
                Array.Empty<AIToolCall>(), "m", new TokenUsage(1, 1));
        });

        var outcome = await h.Executor.ExecuteTaskAsync(plan, task, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(2, h.Provider.Seen.Count);
    }

    [Fact]
    public async Task ToolEvents_CarryArgumentsPayload()
    {
        var note = Path.Combine(_workspace, "note.txt");
        await File.WriteAllTextAsync(note, "secret-content");
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var started = new List<AgentEvent>();
        using var _ = bus.Subscribe(AgentEventType.ToolStarted, (e, _) =>
        {
            started.Add(e);
            return Task.CompletedTask;
        });
        var h = Create(new AllowConfirmation(), confirmWrites: false, events: bus);
        var (plan, task) = await SeedAsync(h.Sessions, "Read the note");

        h.Provider.Enqueue(_ => new AIResponse("reading",
            new[] { new AIToolCall("c1", "ReadFile", $"{{\"path\":{Json(note)}}}") },
            "m", new TokenUsage(1, 1)));
        h.Provider.Enqueue(_ => new AIResponse("done.",
            Array.Empty<AIToolCall>(), "m", new TokenUsage(1, 1)));

        var outcome = await h.Executor.ExecuteTaskAsync(plan, task, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);
        var read = Assert.Single(started);
        Assert.Contains("ReadFile", read.Summary);
        Assert.Contains("note.txt", read.PayloadJson);
    }

    [Fact]
    public async Task WriteFlow_ConfirmationAllow_WritesFile()
    {
        var h = Create(new AllowConfirmation(), confirmWrites: true);
        var (plan, task) = await SeedAsync(h.Sessions, "Write output");
        var target = Path.Combine(_workspace, "out.txt");

        h.Provider.Enqueue(_ => new AIResponse("writing",
            new[] { new AIToolCall("c1", "WriteFile",
                $"{{\"path\":{Json(target)},\"content\":\"data-123\"}}") },
            "m", new TokenUsage(1, 1)));
        h.Provider.Enqueue(_ => new AIResponse("Written.", Array.Empty<AIToolCall>(), "m", new TokenUsage(1, 1)));

        var outcome = await h.Executor.ExecuteTaskAsync(plan, task, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("data-123", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task WriteFlow_ConfirmationDeny_BlocksWrite_ModelAdapts()
    {
        var h = Create(new DenyConfirmation(), confirmWrites: true);
        var (plan, task) = await SeedAsync(h.Sessions, "Write output");
        var target = Path.Combine(_workspace, "blocked.txt");

        h.Provider.Enqueue(_ => new AIResponse("writing",
            new[] { new AIToolCall("c1", "WriteFile",
                $"{{\"path\":{Json(target)},\"content\":\"x\"}}") },
            "m", new TokenUsage(1, 1)));
        h.Provider.Enqueue(req =>
        {
            var toolMsg = req.Messages.Last(m => m.Role == AIRole.Tool);
            Assert.Contains("Denied", toolMsg.Content);
            return new AIResponse("Understood, skipping.", Array.Empty<AIToolCall>(), "m", new TokenUsage(1, 1));
        });

        var outcome = await h.Executor.ExecuteTaskAsync(plan, task, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task MissingModelConfig_ReturnsEnvironmentFailure()
    {
        var h = Create(new AllowConfirmation(), confirmWrites: false);
        var (plan, task) = await SeedAsync(h.Sessions, "Anything");
        // Reconfigura sin modelo: reconstruye el ejecutor con opciones vacías.
        var noModel = Options.Create(new CaAIAOptions());
        var executor = new LlmTaskExecutor(
            h.Sessions,
            new Infrastructure.AI.ProviderRegistry(NullLogger<Infrastructure.AI.ProviderRegistry>.Instance),
            new Application.Services.UserPreferences(noModel, new MemorySettingsStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Application.Services.UserPreferences>.Instance),
            new ToolRegistry(), new ToolPermissionService(),
            new WorkspaceContextBuilder(new WorkspaceReader()),
            new AllowConfirmation(), new InMemoryMemoryStore(),
            new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance),
            noModel, NullLogger<LlmTaskExecutor>.Instance);
        var outcome = await executor.ExecuteTaskAsync(plan, task, CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Equal(FailureCategory.EnvironmentFailure, outcome.Category);
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    // ---- Doubles ----

    private sealed class ScriptedLlmProvider : IAIProvider
    {
        private readonly Queue<Func<AIRequest, AIResponse>> _script = new();
        public readonly List<AIRequest> Seen = new();
        public void Enqueue(Func<AIRequest, AIResponse> fn) => _script.Enqueue(fn);
        public string Id => "fake-llm";
        public string DisplayName => "fake-llm";
        public ProviderCapabilities Capabilities =>
            ProviderCapabilities.Streaming | ProviderCapabilities.Tools | ProviderCapabilities.StructuredOutput;

        public Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct)
        {
            Seen.Add(request);
            Assert.True(_script.Count > 0, "Provider called more times than scripted.");
            return Task.FromResult(_script.Dequeue()(request));
        }

        public IAsyncEnumerable<AIStreamChunk> StreamAsync(AIRequest request, CancellationToken ct) =>
            AsyncEnumerable.Empty<AIStreamChunk>();
        public Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<AIModel>>(Array.Empty<AIModel>());
        public Task<bool> CheckHealthAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class AllowConfirmation : IUserConfirmation
    {
        public Task<bool> RequestAsync(string title, string details, CancellationToken ct) =>
            Task.FromResult(true);
    }

    private sealed class DenyConfirmation : IUserConfirmation
    {
        public Task<bool> RequestAsync(string title, string details, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private sealed class MemorySettingsStore : Domain.Persistence.ISettingsStore
    {
        private readonly Dictionary<string, string> _data = new();
        public Task<string?> GetAsync(string key, CancellationToken ct) =>
            Task.FromResult<string?>(_data.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct)
        {
            _data[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct)
        {
            _data.Remove(key);
            return Task.CompletedTask;
        }
    }
}
