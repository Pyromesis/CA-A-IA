// CA-A-IA · Fase 0 — Tests del motor con ejecutor guionizado: éxito, reparación,
// escalado de fallos no reparables, auditoría con ampliación y cancelación.

using CaAIA.Agent.Execution;
using CaAIA.Agent.Verification;
using CaAIA.Application.Configuration;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Events;
using CaAIA.Domain.Execution;
using CaAIA.Domain.Persistence;
using CaAIA.Domain.Planning;
using CaAIA.Infrastructure.Events;
using CaAIA.Infrastructure.Persistence.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaAIA.Tests.Integration;

public sealed class EngineTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"ca-a-ia-eng-{Guid.NewGuid():N}");
    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch (Exception) { }
    }

    private sealed record Ctx(
        IAgentSessionStore Sessions, IPlanStore Plans, ICheckpointStore Checkpoints, IEventBus Events);

    private Ctx Stores()
    {
        var options = Options.Create(new CaAIAOptions
        {
            Persistence = new PersistenceSettings { DataPath = _dataPath },
        });
        var db = new SqliteConnectionFactory(options, NullLogger<SqliteConnectionFactory>.Instance);
        return new Ctx(
            new SqliteAgentSessionStore(db),
            new SqlitePlanStore(db),
            new SqliteCheckpointStore(db),
            new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance));
    }

    private static IOptions<CaAIAOptions> EngineOptions() => Options.Create(new CaAIAOptions
    {
        Execution = new ExecutionSettings
        {
            OperationTimeoutSeconds = 30,
            GlobalTimeoutMinutes = 1,
            HeartbeatSeconds = 5,
            ToolTimeoutSeconds = 10,
        },
    });

    private static async Task<Guid> SeedAsync(Ctx ctx, Plan plan)
    {
        var session = new AgentSession { WorkspacePath = Path.GetTempPath(), UserRequest = plan.Goal };
        await ctx.Sessions.SaveAsync(session, CancellationToken.None);
        var withSession = new Plan
        {
            SessionId = session.Id,
            Goal = plan.Goal,
            Requirements = plan.Requirements,
            FinalAcceptanceCriteria = plan.FinalAcceptanceCriteria,
            Tasks = plan.Tasks,
        };
        withSession.SetQuestions(Array.Empty<Clarification>());
        withSession.MarkInReview();
        withSession.MarkApproved();
        withSession.MarkExecuting();
        await ctx.Plans.SaveAsync(withSession, CancellationToken.None);
        session.AttachPlan(withSession.Id);
        await ctx.Sessions.SaveAsync(session, CancellationToken.None);
        return session.Id;
    }

    [Fact]
    public async Task Run_HappyPath_CompletesPlanAndSession()
    {
        var ctx = Stores();
        var plan = new Plan
        {
            Goal = "Do thing",
            Requirements = new[] { "Do thing" },
            Tasks = new List<AgentTask>
            {
                new() { Title = "Do thing", Description = "Do thing now" },
            },
        };
        var sessionId = await SeedAsync(ctx, plan);
        var engine = new AgentExecutionEngine(sessionId, ctx.Sessions, ctx.Plans, ctx.Checkpoints,
            ctx.Events, new ScriptedExecutor(_ => Task.FromResult(new TaskExecutionOutcome(true))),
            new SatisfiedVerifier(), new InstantRepairPolicy(), EngineOptions(),
            NullLogger<AgentExecutionEngine>.Instance);

        await engine.RunAsync(sessionId, CancellationToken.None);

        Assert.Equal(AgentState.Completed, engine.StateMachine.Current);
        var session = await ctx.Sessions.LoadAsync(sessionId, CancellationToken.None);
        Assert.NotNull(session);
        Assert.False(session.IsActive);
        var checkpoint = await ctx.Checkpoints.LoadLatestAsync(sessionId, CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(AgentState.Completed, checkpoint.State);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Run_TransientCodeError_RepairsAndCompletes()
    {
        var ctx = Stores();
        var plan = new Plan
        {
            Goal = "Flaky thing",
            Requirements = new[] { "Flaky thing" },
            Tasks = new List<AgentTask> { new() { Title = "Flaky thing", MaxAttempts = 3 } },
        };
        var sessionId = await SeedAsync(ctx, plan);
        var calls = 0;
        var engine = new AgentExecutionEngine(sessionId, ctx.Sessions, ctx.Plans, ctx.Checkpoints,
            ctx.Events,
            new ScriptedExecutor(_ =>
            {
                calls++;
                return Task.FromResult(calls == 1
                    ? new TaskExecutionOutcome(false, "null ref", FailureCategory.CodeError)
                    : new TaskExecutionOutcome(true));
            }),
            new SatisfiedVerifier(), new InstantRepairPolicy(), EngineOptions(),
            NullLogger<AgentExecutionEngine>.Instance);

        await engine.RunAsync(sessionId, CancellationToken.None);

        Assert.Equal(AgentState.Completed, engine.StateMachine.Current);
        Assert.Equal(2, calls);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Run_NonRepairableFailure_EscalatesPlanToFailed()
    {
        var ctx = Stores();
        var plan = new Plan
        {
            Goal = "Blocked thing",
            Requirements = new[] { "Blocked thing" },
            Tasks = new List<AgentTask> { new() { Title = "Blocked thing", MaxAttempts = 3 } },
        };
        var sessionId = await SeedAsync(ctx, plan);
        var engine = new AgentExecutionEngine(sessionId, ctx.Sessions, ctx.Plans, ctx.Checkpoints,
            ctx.Events,
            new ScriptedExecutor(_ => Task.FromResult(
                new TaskExecutionOutcome(false, "denied", FailureCategory.PermissionFailure))),
            new SatisfiedVerifier(), new InstantRepairPolicy(), EngineOptions(),
            NullLogger<AgentExecutionEngine>.Instance);

        await engine.RunAsync(sessionId, CancellationToken.None);

        // Sin reintentos a ciegas ante permisos: el plan escala a Failed para intervención.
        Assert.Equal(AgentState.Failed, engine.StateMachine.Current);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Run_FinalAuditGap_ExtendsPlan_ThenCompletes()
    {
        var ctx = Stores();
        var plan = new Plan
        {
            Goal = "Login and guide",
            Requirements = new[] { "Implement login flow", "Write user guide" },
            Tasks = new List<AgentTask>
            {
                new() { Title = "Implement login flow", Description = "login flow" },
            },
        };
        var sessionId = await SeedAsync(ctx, plan);
        var auditor = new FinalPlanAuditor(NullLogger<FinalPlanAuditor>.Instance);
        var engine = new AgentExecutionEngine(sessionId, ctx.Sessions, ctx.Plans, ctx.Checkpoints,
            ctx.Events, new ScriptedExecutor(_ => Task.FromResult(new TaskExecutionOutcome(true))),
            auditor, new InstantRepairPolicy(), EngineOptions(),
            NullLogger<AgentExecutionEngine>.Instance);

        await engine.RunAsync(sessionId, CancellationToken.None);

        Assert.Equal(AgentState.Completed, engine.StateMachine.Current);
        var session = await ctx.Sessions.LoadAsync(sessionId, CancellationToken.None);
        var stored = await ctx.Plans.LoadAsync(session!.PlanId!.Value, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Revision); // la auditoría amplió el plan una vez
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Run_RepeatedAuditGap_DoesNotExplode()
    {
        // El juez repite la misma queja: solo se amplía una vez; la segunda
        // ronda la detecta ya abordada y converge en vez de crecer sin fin.
        var ctx = Stores();
        var plan = new Plan
        {
            Goal = "Thing",
            Requirements = new[] { "Thing" },
            Tasks = new List<AgentTask> { new() { Title = "Thing", Description = "Thing" } },
        };
        var sessionId = await SeedAsync(ctx, plan);
        var engine = new AgentExecutionEngine(sessionId, ctx.Sessions, ctx.Plans, ctx.Checkpoints,
            ctx.Events, new ScriptedExecutor(_ => Task.FromResult(new TaskExecutionOutcome(true))),
            new ConstantGapVerifier("missing widget"), new InstantRepairPolicy(), EngineOptions(),
            NullLogger<AgentExecutionEngine>.Instance);

        await engine.RunAsync(sessionId, CancellationToken.None);

        Assert.Equal(AgentState.Completed, engine.StateMachine.Current);
        var session = await ctx.Sessions.LoadAsync(sessionId, CancellationToken.None);
        var stored = await ctx.Plans.LoadAsync(session!.PlanId!.Value, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Revision);
        Assert.Equal(2, stored.Tasks.Count); // 1 original + 1 gap, sin duplicados
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Run_EverChangingGaps_FailsAfterMaxRounds()
    {
        // Juez quejica con paráfrasis nuevas cada ronda: tras MaxAuditRounds se
        // falla honestamente en vez de alargar el plan hasta el timeout global.
        var ctx = Stores();
        var plan = new Plan
        {
            Goal = "Thing",
            Requirements = new[] { "Thing" },
            Tasks = new List<AgentTask> { new() { Title = "Thing", Description = "Thing" } },
        };
        var sessionId = await SeedAsync(ctx, plan);
        var options = Options.Create(new CaAIAOptions
        {
            Execution = new ExecutionSettings
            {
                OperationTimeoutSeconds = 30,
                GlobalTimeoutMinutes = 1,
                HeartbeatSeconds = 5,
                ToolTimeoutSeconds = 10,
                MaxAuditRounds = 1,
            },
        });
        var engine = new AgentExecutionEngine(sessionId, ctx.Sessions, ctx.Plans, ctx.Checkpoints,
            ctx.Events, new ScriptedExecutor(_ => Task.FromResult(new TaskExecutionOutcome(true))),
            new RotatingGapVerifier(), new InstantRepairPolicy(), options,
            NullLogger<AgentExecutionEngine>.Instance);

        await engine.RunAsync(sessionId, CancellationToken.None);

        Assert.Equal(AgentState.Failed, engine.StateMachine.Current);
        var session = await ctx.Sessions.LoadAsync(sessionId, CancellationToken.None);
        var stored = await ctx.Plans.LoadAsync(session!.PlanId!.Value, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Revision); // una ampliación y basta
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Run_OperationTimeout_BecomesFailure_NotPoison()
    {
        // El timeout de operación (1 s) NO es pausa/cancelación: antes escapaba
        // como OperationCanceledException, dejaba el motor sin estado terminal y
        // el siguiente Run moría con transición ilegal. Ahora es un fallo más.
        var ctx = Stores();
        var plan = new Plan
        {
            Goal = "Slow thing",
            Requirements = new[] { "Slow thing" },
            Tasks = new List<AgentTask> { new() { Title = "Slow thing", Description = "Slow" } },
        };
        var sessionId = await SeedAsync(ctx, plan);
        var options = Options.Create(new CaAIAOptions
        {
            Execution = new ExecutionSettings
            {
                OperationTimeoutSeconds = 1,
                GlobalTimeoutMinutes = 1,
                HeartbeatSeconds = 5,
                ToolTimeoutSeconds = 10,
            },
        });
        var engine = new AgentExecutionEngine(sessionId, ctx.Sessions, ctx.Plans, ctx.Checkpoints,
            ctx.Events,
            new ScriptedExecutor(async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new TaskExecutionOutcome(true);
            }),
            new SatisfiedVerifier(), new NeverRepairPolicy(), options,
            NullLogger<AgentExecutionEngine>.Instance);

        await engine.RunAsync(sessionId, CancellationToken.None);
        Assert.Equal(AgentState.Failed, engine.StateMachine.Current);

        // Reutilizable: el segundo Run no lanza transición ilegal.
        await engine.RunAsync(sessionId, CancellationToken.None);
        Assert.Equal(AgentState.Failed, engine.StateMachine.Current);
        await engine.DisposeAsync();
    }

    [Fact]
    public void FilterAlreadyAddressed_SkipsDuplicateGaps()
    {
        var plan = new Plan
        {
            Goal = "g",
            Tasks = new List<AgentTask>
            {
                new() { Title = "Audit gap: the widget frobnicate handler", Description = "x" },
            },
        };
        var filtered = FinalPlanAuditor.FilterAlreadyAddressed(plan,
            new[] { "Audit gap: The widget frobnicate handler!", "brand new unpaid invoice report" });
        Assert.Single(filtered);
        Assert.Contains("invoice", filtered[0]);
    }

    [Fact]
    public async Task Cancel_DuringExecution_StopsGracefully()
    {
        var ctx = Stores();
        var plan = new Plan
        {
            Goal = "Long thing",
            Requirements = new[] { "Long thing" },
            Tasks = new List<AgentTask> { new() { Title = "Long thing" } },
        };
        var sessionId = await SeedAsync(ctx, plan);
        var engine = new AgentExecutionEngine(sessionId, ctx.Sessions, ctx.Plans, ctx.Checkpoints,
            ctx.Events,
            new ScriptedExecutor(async task =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5), task);
                return new TaskExecutionOutcome(true);
            }),
            new SatisfiedVerifier(), new InstantRepairPolicy(), EngineOptions(),
            NullLogger<AgentExecutionEngine>.Instance);

        var run = engine.RunAsync(sessionId, CancellationToken.None);
        await Task.Delay(500);
        await engine.CancelAsync(CancellationToken.None);
        await run;

        Assert.Equal(AgentState.Cancelled, engine.StateMachine.Current);
        await engine.DisposeAsync();
    }

    // ---- Doubles ----

    [Fact]
    public void Watchdog_DetectsStall_AfterConfiguredTimeout()
    {
        var ctx = Stores();
        var fastOptions = Options.Create(new CaAIAOptions
        {
            Execution = new ExecutionSettings
            {
                OperationTimeoutSeconds = 30,
                GlobalTimeoutMinutes = 1,
                HeartbeatSeconds = 5,
                StallDetectionSeconds = 10,
            },
        });
        var engine = new AgentExecutionEngine(Guid.NewGuid(), ctx.Sessions, ctx.Plans, ctx.Checkpoints,
            ctx.Events, new ScriptedExecutor(_ => Task.FromResult(new TaskExecutionOutcome(true))),
            new SatisfiedVerifier(), new InstantRepairPolicy(), fastOptions,
            NullLogger<AgentExecutionEngine>.Instance);

        Assert.False(engine.IsStalledAt(DateTimeOffset.UtcNow));
        Assert.True(engine.IsStalledAt(DateTimeOffset.UtcNow.AddSeconds(11)));
    }

    [Fact]
    public async Task EngineEvents_CarrySessionCorrelation()
    {
        var ctx = Stores();
        var plan = new Plan
        {
            Goal = "Correlated",
            Requirements = new[] { "Correlated" },
            Tasks = new List<AgentTask> { new() { Title = "Correlated" } },
        };
        var sessionId = await SeedAsync(ctx, plan);
        var seen = new List<Domain.Events.AgentEvent>();
        using var _ = ((InMemoryEventBus)ctx.Events).SubscribeAll((e, _) =>
        {
            seen.Add(e);
            return Task.CompletedTask;
        });
        var engine = new AgentExecutionEngine(sessionId, ctx.Sessions, ctx.Plans, ctx.Checkpoints,
            ctx.Events, new ScriptedExecutor(_ => Task.FromResult(new TaskExecutionOutcome(true))),
            new SatisfiedVerifier(), new InstantRepairPolicy(), EngineOptions(),
            NullLogger<AgentExecutionEngine>.Instance);

        await engine.RunAsync(sessionId, CancellationToken.None);

        var taskStarted = Assert.Single(seen, e => e.Type == Domain.Enums.AgentEventType.TaskStarted);
        Assert.Equal(sessionId, taskStarted.Correlation?.SessionId);
        var transitions = seen.Where(e => e.Type == Domain.Enums.AgentEventType.StateChanged).ToList();
        Assert.NotEmpty(transitions);
        Assert.All(transitions, t => Assert.Equal(sessionId, t.Correlation?.SessionId));
        await engine.DisposeAsync();
    }

    // ---- Doubles ----

    private sealed class ScriptedExecutor : ITaskExecutor
    {
        private readonly Func<CancellationToken, Task<TaskExecutionOutcome>> _fn;
        public ScriptedExecutor(Func<CancellationToken, Task<TaskExecutionOutcome>> fn) => _fn = fn;
        public Task<TaskExecutionOutcome> ExecuteTaskAsync(Plan plan, AgentTask task, CancellationToken ct) => _fn(ct);
    }

    private sealed class SatisfiedVerifier : IPlanVerifier
    {
        public Task<VerificationReport> AuditAsync(
            string userRequest, Plan plan, IReadOnlyList<string> testEvidence, CancellationToken ct) =>
            Task.FromResult(new VerificationReport(plan.Id, plan.Revision, true,
                Array.Empty<string>(), new[] { "stub: satisfied" }, DateTimeOffset.UtcNow));
    }

    private sealed class ConstantGapVerifier : IPlanVerifier
    {
        private readonly string _gap;
        public ConstantGapVerifier(string gap) => _gap = gap;
        public Task<VerificationReport> AuditAsync(
            string userRequest, Plan plan, IReadOnlyList<string> testEvidence, CancellationToken ct) =>
            Task.FromResult(new VerificationReport(plan.Id, plan.Revision, false,
                new[] { _gap }, new[] { "stub: gap" }, DateTimeOffset.UtcNow));
    }

    private sealed class RotatingGapVerifier : IPlanVerifier
    {
        private int _n;
        public Task<VerificationReport> AuditAsync(
            string userRequest, Plan plan, IReadOnlyList<string> testEvidence, CancellationToken ct) =>
            Task.FromResult(new VerificationReport(plan.Id, plan.Revision, false,
                new[] { $"completely new concern number {Interlocked.Increment(ref _n)} about moonbeams" },
                new[] { "stub: rotating gap" }, DateTimeOffset.UtcNow));
    }

    private sealed class InstantRepairPolicy : IRepairPolicy
    {
        private readonly Domain.Execution.IRepairPolicy _inner = new CaAIA.Agent.Policies.DefaultRepairPolicy();
        public bool ShouldRepair(FailureCategory category, int attempts, int maxAttempts) =>
            _inner.ShouldRepair(category, attempts, maxAttempts);
        public TimeSpan DelayBeforeRetry(FailureCategory category, int attempts) => TimeSpan.Zero;
    }

    private sealed class NeverRepairPolicy : IRepairPolicy
    {
        public bool ShouldRepair(FailureCategory category, int attempts, int maxAttempts) => false;
        public TimeSpan DelayBeforeRetry(FailureCategory category, int attempts) => TimeSpan.Zero;
    }
}
