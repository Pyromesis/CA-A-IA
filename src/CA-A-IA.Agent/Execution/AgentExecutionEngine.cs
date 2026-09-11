// CA-A-IA · Fase 0 — Motor de ejecución: estados + checkpoints + reintentos + auditoría final.
// Desacoplado de WinUI. La ejecución REAL de cada tarea vive en ITaskExecutor (fase posterior).

using CaAIA.Agent.StateMachine;
using CaAIA.Agent.Verification;
using CaAIA.Application.Configuration;
using CaAIA.Domain.Correlation;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Events;
using CaAIA.Domain.Execution;
using CaAIA.Domain.Persistence;
using CaAIA.Domain.Planning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Agent.Execution;

/// <summary>
/// Ciclo implementado: PreparingExecution → Executing → Testing → VerifyingTask → AdvancingTask
/// → … → FinalVerification → Completed, con rama AnalyzingFailure → Repairing → Retesting y
/// política de reparación por categoría de fallo (§19, §20). Cada transición genera checkpoint.
/// Pausa/cancelación cooperativas: el bucle observa el token y conserva el checkpoint.
/// </summary>
public sealed class AgentExecutionEngine : IAgentExecutionEngine, IAsyncDisposable
{
    private readonly Guid _sessionId;
    private readonly IAgentSessionStore _sessions;
    private readonly IPlanStore _plans;
    private readonly ICheckpointStore _checkpoints;
    private readonly IEventBus _events;
    private readonly ITaskExecutor _executor;
    private readonly IPlanVerifier _verifier;
    private readonly IRepairPolicy _repair;
    private readonly ExecutionSettings _settings;
    private readonly ILogger<AgentExecutionEngine> _log;
    private readonly CorrelationContext _correlation;

    private readonly AgentStateMachine _machine;
    private readonly CancellationTokenSource _internalCts = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private Timer? _heartbeat;
    private Timer? _watchdog;
    private DateTimeOffset _lastProgressUtc = DateTimeOffset.UtcNow;
    private Guid? _currentTaskId;
    private Plan? _activePlan;
    private int _auditRounds;
    private readonly List<string> _taskEvidence = new();

    public Guid ExecutionId { get; } = Guid.NewGuid();
    public IAgentStateMachine StateMachine => _machine;

    public AgentExecutionEngine(
        Guid sessionId,
        IAgentSessionStore sessions,
        IPlanStore plans,
        ICheckpointStore checkpoints,
        IEventBus events,
        ITaskExecutor executor,
        IPlanVerifier verifier,
        IRepairPolicy repair,
        IOptions<CaAIAOptions> options,
        ILogger<AgentExecutionEngine> log,
        AgentState initialState = AgentState.Idle)
    {
        _sessionId = sessionId;
        _sessions = sessions;
        _plans = plans;
        _checkpoints = checkpoints;
        _events = events;
        _executor = executor;
        _verifier = verifier;
        _repair = repair;
        _settings = options.Value.Execution;
        _log = log;
        _correlation = CorrelationContext.Create(sessionId, ExecutionId);
        _machine = new AgentStateMachine(initialState);
        _machine.Transitioned += (_, t) =>
            _events.Publish(AgentEvent.Create(AgentEventType.StateChanged, _correlation, $"{t.From} -> {t.To}: {t.Reason}"));
    }

    public async Task RunAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (sessionId != _sessionId)
        {
            throw new ArgumentException("Engine is bound to a different session.", nameof(sessionId));
        }

        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Engine is already running for this session.");
        }

        using var globalTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, _settings.GlobalTimeoutMinutes)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _internalCts.Token, globalTimeout.Token);
        var ct = linked.Token;

        StartHeartbeat();
        StartWatchdog();
        try
        {
            await Go(AgentState.PreparingExecution, "run started", ct).ConfigureAwait(false);

            Plan plan;
            try
            {
                plan = await LoadApprovedPlanAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fallo de arranque (sesión/plan ausente, plan no aprobado): antes la
                // excepción salía sin estado terminal y el motor quedaba envenenado
                // (el 2º Run intentaba PreparingExecution→PreparingExecution, ilegal).
                _log.LogError(ex, "Execution {ExecutionId} failed to start", ExecutionId);
                await GoSafeAsync(AgentState.Failed, $"startup failed: {ex.Message}").ConfigureAwait(false);
                return;
            }

            _activePlan = plan;
            _auditRounds = 0;
            _taskEvidence.Clear();
            try
            {
                await MainLoopAsync(plan, ct).ConfigureAwait(false);
            }
            finally
            {
                _activePlan = null;
            }
        }
        catch (OperationCanceledException) when (globalTimeout.IsCancellationRequested
            && !_internalCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Timeout global: antes caía al finally en silencio, sin Failed ni auditoría.
            _log.LogError("Execution {ExecutionId} exceeded global timeout of {Timeout}min in state {State}",
                ExecutionId, _settings.GlobalTimeoutMinutes, _machine.Current);
            await GoSafeAsync(AgentState.Failed,
                $"global timeout after {_settings.GlobalTimeoutMinutes}min in {_machine.Current}").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_internalCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Pausa o cancelación solicitada vía PauseAsync/CancelAsync: el estado ya se fijó allí.
            _log.LogInformation("Execution {ExecutionId} stopped by request in state {State}", ExecutionId, _machine.Current);
        }
        finally
        {
            StopHeartbeat();
            StopWatchdog();
            _runGate.Release();
        }
    }

    /// <summary>
    /// Transición best-effort a estado terminal cuando el flujo normal ya falló:
    /// nunca lanza (el checkpoint interno ya es tolerante a fallos).
    /// </summary>
    private async Task GoSafeAsync(AgentState next, string reason)
    {
        try
        {
            if (!AgentTransitions.Terminal.Contains(_machine.Current))
            {
                _machine.TransitionTo(next, reason);
                _lastProgressUtc = DateTimeOffset.UtcNow;
            }

            await CheckpointAsync(reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GoSafe to {State} failed", next);
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        GoSync(AgentState.Paused, "pause requested");
        _internalCts.Cancel();
        _events.Publish(AgentEvent.Create(AgentEventType.AgentPaused, _correlation, $"Execution {ExecutionId} paused."));
        return CheckpointAsync("paused");
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        if (_machine.Current != AgentState.Paused)
        {
            throw new InvalidOperationException($"Cannot resume from {_machine.Current} (requires Paused).");
        }

        GoSync(AgentState.Recovering, "resume requested");
        _events.Publish(AgentEvent.Create(AgentEventType.AgentResumed, _correlation, $"Execution {ExecutionId} resuming."));
        // NOTA: _internalCts ya consumido; la factoría crea un motor nuevo al reanudar (ver AgentEngineFactory).
        return CheckpointAsync("resumed");
    }

    public Task CancelAsync(CancellationToken cancellationToken)
    {
        if (!AgentTransitions.Terminal.Contains(_machine.Current))
        {
            GoSync(AgentState.Cancelled, "cancel requested");
        }

        _internalCts.Cancel();
        _events.Publish(AgentEvent.Create(AgentEventType.AgentStopped, _correlation, $"Execution {ExecutionId} cancelled."));
        return CheckpointAsync("cancelled");
    }

    // ---- Bucle principal ----

    private async Task MainLoopAsync(Plan plan, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var statuses = plan.Tasks.ToDictionary(t => t.Id, t => t.Status);

            var next = plan.Tasks.FirstOrDefault(t => t.IsReady(statuses));
            if (next is null)
            {
                if (plan.AllTasksClosed())
                {
                    await FinalAuditAsync(plan, ct).ConfigureAwait(false);
                    // Solo se sigue si la auditoría amplió el plan (termina en
                    // AdvancingTask): Completed/Failed/Cancelled salen del bucle.
                    // Antes solo se miraba Completed y tras un Fail se reintentaba
                    // auditar desde Failed (transición ilegal).
                    if (_machine.Current != AgentState.AdvancingTask)
                    {
                        return;
                    }

                    continue;
                }

                await FailAsync(plan, "No ready tasks but plan is not closed (dependency deadlock).", ct)
                    .ConfigureAwait(false);
                return;
            }

            await ExecuteOneAsync(plan, next, ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteOneAsync(Plan plan, AgentTask task, CancellationToken ct)
    {
        _currentTaskId = task.Id;
        task.MarkStarted();
        await _plans.SaveAsync(plan, ct).ConfigureAwait(false);

        await Go(AgentState.Executing, $"task '{task.Title}'", ct).ConfigureAwait(false);
        _events.Publish(AgentEvent.Create(AgentEventType.TaskStarted, _correlation, task.Title));

        TaskExecutionOutcome outcome;
        try
        {
            using var opTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Max(10, _settings.OperationTimeoutSeconds)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, opTimeout.Token);
            outcome = await _executor.ExecuteTaskAsync(plan, task, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            outcome = new TaskExecutionOutcome(false, ex.Message, FailureCategory.ToolFailure);
        }

        if (outcome.Success)
        {
            await Go(AgentState.Testing, $"task '{task.Title}'", ct).ConfigureAwait(false);
            await Go(AgentState.VerifyingTask, $"task '{task.Title}'", ct).ConfigureAwait(false);
            task.MarkCompleted();
            await _plans.SaveAsync(plan, ct).ConfigureAwait(false);
            RememberTaskEvidence(task);
            _events.Publish(AgentEvent.Create(AgentEventType.TaskCompleted, _correlation, task.Title));
            await Go(AgentState.AdvancingTask, "task verified", ct).ConfigureAwait(false);
            return;
        }

        await RepairLoopAsync(plan, task, outcome, ct).ConfigureAwait(false);
    }

    private async Task RepairLoopAsync(Plan plan, AgentTask task, TaskExecutionOutcome first, CancellationToken ct)
    {
        var outcome = first;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Go(AgentState.AnalyzingFailure, $"{outcome.Category}: {outcome.Error}", ct).ConfigureAwait(false);
            task.MarkFailed(outcome.Error ?? "Unknown failure", outcome.Category);
            await _plans.SaveAsync(plan, ct).ConfigureAwait(false);
            _events.Publish(AgentEvent.Create(AgentEventType.TaskFailed, _correlation, $"{task.Title}: {outcome.Error}"));

            if (!_repair.ShouldRepair(outcome.Category, task.Attempts, task.MaxAttempts))
            {
                _log.LogWarning("Task '{Title}' not repairable ({Category}); advancing", task.Title, outcome.Category);
                await Go(AgentState.AdvancingTask, "task failed, not repairable", ct).ConfigureAwait(false);
                return;
            }

            var delay = _repair.DelayBeforeRetry(outcome.Category, task.Attempts);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            await Go(AgentState.Repairing, $"task '{task.Title}' attempt {task.Attempts + 1}", ct).ConfigureAwait(false);
            _events.Publish(AgentEvent.Create(AgentEventType.RepairStarted, _correlation, task.Title));

            task.MarkStarted(); // nuevo intento
            await _plans.SaveAsync(plan, ct).ConfigureAwait(false);
            await Go(AgentState.Retesting, $"task '{task.Title}'", ct).ConfigureAwait(false);

            try
            {
                outcome = await _executor.ExecuteTaskAsync(plan, task, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // La cancelación no es un fallo reparable: propagar sin marcar TaskFailed
                // ni dar una vuelta extra de reparación.
                throw;
            }
            catch (Exception ex)
            {
                outcome = new TaskExecutionOutcome(false, ex.Message, FailureCategory.ToolFailure);
            }

            _events.Publish(AgentEvent.Create(AgentEventType.RepairCompleted, _correlation,
                $"{task.Title}: {(outcome.Success ? "repaired" : outcome.Error)}"));

            if (outcome.Success)
            {
                await Go(AgentState.VerifyingTask, $"task '{task.Title}' (repaired)", ct).ConfigureAwait(false);
                task.MarkCompleted();
                await _plans.SaveAsync(plan, ct).ConfigureAwait(false);
                RememberTaskEvidence(task);
                _events.Publish(AgentEvent.Create(AgentEventType.TaskCompleted, _correlation, task.Title));
                await Go(AgentState.AdvancingTask, "repaired task verified", ct).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>Evidencia de ejecución para la auditoría (qué se hizo, no solo el plan).</summary>
    private void RememberTaskEvidence(AgentTask task)
    {
        if (_taskEvidence.Count < 200)
        {
            _taskEvidence.Add($"Task completed: {task.Title}");
        }
    }

    private async Task FinalAuditAsync(Plan plan, CancellationToken ct)
    {
        await Go(AgentState.FinalVerification, $"audit plan rev {plan.Revision}", ct).ConfigureAwait(false);
        plan.MarkAuditing();
        await _plans.SaveAsync(plan, ct).ConfigureAwait(false);

        VerificationReport report;
        AgentSession? session = null;
        try
        {
            session = await _sessions.LoadAsync(_sessionId, ct).ConfigureAwait(false);
            report = await _verifier.AuditAsync(
                session?.UserRequest ?? string.Empty, plan,
                new List<string>(_taskEvidence), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Verifier que lanza: antes el motor quedaba en FinalVerification no-terminal.
            _log.LogError(ex, "Execution {ExecutionId} final audit failed", ExecutionId);
            await FailAsync(plan, $"final audit error: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }

        if (report.IsSatisfied)
        {
            plan.MarkCompleted();
            await _plans.SaveAsync(plan, ct).ConfigureAwait(false);
            await Go(AgentState.Completed, "final audit satisfied", ct).ConfigureAwait(false);
            if (session is not null)
            {
                session.RecordState(AgentState.Completed);
                session.Close();
                await _sessions.SaveAsync(session, ct).ConfigureAwait(false);
            }

            return;
        }

        // Requisitos pendientes → tareas adicionales y vuelta a Executing (§9),
        // con tres frenos contra el bucle infinito (el juez, sobre todo el
        // semántico, puede no converger nunca: paráfrasis nuevas cada ronda):
        // 1) tope de gaps por ronda; 2) sin duplicados ya abordados;
        // 3) rondas máximas, tras las cuales se falla honestamente.
        _auditRounds++;
        var freshGaps = FinalPlanAuditor.FilterAlreadyAddressed(plan, report.MissingRequirements);
        if (freshGaps.Count < report.MissingRequirements.Count)
        {
            _log.LogInformation(
                "Final audit round {Round}: {Duplicates} gaps already addressed by existing tasks; {Fresh} new.",
                _auditRounds, report.MissingRequirements.Count - freshGaps.Count, freshGaps.Count);
        }

        if (freshGaps.Count == 0)
        {
            // Sin nada nuevo que hacer: el trabajo se intentó y repetirlo es fútil.
            // Se cierra con aviso en el log (no se miente: la evidencia queda en el
            // informe y el estado Completed llega por esta vía documentada).
            _log.LogWarning(
                "Execution {ExecutionId} audit converged with {Gaps} already-addressed gaps; completing.",
                ExecutionId, report.MissingRequirements.Count);
            plan.MarkCompleted();
            await _plans.SaveAsync(plan, ct).ConfigureAwait(false);
            await Go(AgentState.Completed, "final audit converged (gaps already addressed)", ct)
                .ConfigureAwait(false);
            if (session is not null)
            {
                session.RecordState(AgentState.Completed);
                session.Close();
                await _sessions.SaveAsync(session, ct).ConfigureAwait(false);
            }

            return;
        }

        var maxRounds = Math.Max(1, _settings.MaxAuditRounds);
        if (_auditRounds > maxRounds)
        {
            await FailAsync(plan,
                $"final audit did not converge after {maxRounds} rounds ({freshGaps.Count} open gaps)",
                ct).ConfigureAwait(false);
            return;
        }

        const int maxGapsPerRound = 10;
        const int maxGapChars = 200;
        var gaps = freshGaps.Take(maxGapsPerRound).ToList();
        if (freshGaps.Count > gaps.Count)
        {
            _log.LogWarning("Final audit reported {Total} gaps; addressing first {Kept}",
                freshGaps.Count, gaps.Count);
        }

        var extra = gaps.Select(m =>
        {
            var requirement = m.Length > maxGapChars ? m[..maxGapChars] + "…" : m;
            return new AgentTask
            {
                Title = $"Audit gap: {requirement}",
                Description = $"Additional task created by final audit (plan rev {plan.Revision}). Requirement: {requirement}",
                MaxAttempts = 3,
            };
        });
        plan.ExtendAfterAudit(extra);
        await _plans.SaveAsync(plan, ct).ConfigureAwait(false);
        // Vuelta al avance (AdvancingTask→Executing por tarea): evita la autotransición Executing→Executing.
        await Go(AgentState.AdvancingTask, $"{gaps.Count} audit gaps to address (round {_auditRounds})", ct).ConfigureAwait(false);
    }

    private async Task FailAsync(Plan plan, string reason, CancellationToken ct)
    {
        await Go(AgentState.Failed, reason, ct).ConfigureAwait(false);
        plan.MarkFailed();
        await _plans.SaveAsync(plan, ct).ConfigureAwait(false);
    }

    private async Task<Plan> LoadApprovedPlanAsync(CancellationToken ct)
    {
        var session = await _sessions.LoadAsync(_sessionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {_sessionId} not found.");
        if (session.PlanId is null)
        {
            throw new InvalidOperationException($"Session {_sessionId} has no plan attached.");
        }

        var plan = await _plans.LoadAsync(session.PlanId.Value, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Plan {session.PlanId} not found.");
        if (plan.Status is not (PlanStatus.Approved or PlanStatus.Executing or PlanStatus.Auditing))
        {
            throw new InvalidOperationException($"Plan {plan.Id} is not approved (status: {plan.Status}).");
        }

        return plan;
    }

    private async Task Go(AgentState next, string? reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _machine.TransitionTo(next, reason);
        _lastProgressUtc = DateTimeOffset.UtcNow;
        await CheckpointAsync(reason).ConfigureAwait(false);
    }

    private void GoSync(AgentState next, string? reason)
    {
        _machine.TransitionTo(next, reason);
    }

    private async Task CheckpointAsync(string? note)
    {
        try
        {
            var session = await _sessions.LoadAsync(_sessionId, CancellationToken.None).ConfigureAwait(false);
            var checkpoint = new Checkpoint(
                Guid.NewGuid(), _sessionId, session?.PlanId, _machine.Current,
                _currentTaskId, 0, DateTimeOffset.UtcNow, note);
            await _checkpoints.SaveCheckpointAsync(checkpoint, CancellationToken.None).ConfigureAwait(false);
            if (session is not null)
            {
                session.RecordState(_machine.Current);
                await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            }

            _events.Publish(AgentEvent.Create(AgentEventType.CheckpointCreated, _correlation,
                $"checkpoint in {_machine.Current}"));
        }
        catch (Exception ex)
        {
            // Un checkpoint que falla no debe tumbar la ejecución; se registra y se continúa.
            _log.LogWarning(ex, "Checkpoint failed in state {State}", _machine.Current);
        }
    }

    private void StartHeartbeat()
    {
        var period = TimeSpan.FromSeconds(Math.Max(5, _settings.HeartbeatSeconds));
        _heartbeat = new Timer(_ => _ = HeartbeatAsync(), null, period, period);
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            var session = await _sessions.LoadAsync(_sessionId, CancellationToken.None).ConfigureAwait(false);
            if (session is not null)
            {
                session.Heartbeat();
                await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Heartbeat failed");
        }
    }

    private void StopHeartbeat()
    {
        try
        {
            _heartbeat?.Dispose();
        }
        catch (Exception)
        {
        }

        _heartbeat = null;
    }

    /// <summary>
    /// Watchdog (§7): si no hay progreso (transiciones) durante StallDetectionSeconds, la
    /// operación se considera bloqueada: se publica, se pasa a Failed y se cancela el CTS
    /// interno para que el bucle salga de forma cooperativa con checkpoint conservado.
    /// Público como API de observabilidad (el timer lo evalúa periódicamente).
    /// </summary>
    public bool IsStalledAt(DateTimeOffset now) =>
        now - _lastProgressUtc > TimeSpan.FromSeconds(Math.Max(10, _settings.StallDetectionSeconds));

    private void StartWatchdog()
    {
        var period = TimeSpan.FromSeconds(Math.Max(15, _settings.StallDetectionSeconds / 4.0));
        _watchdog = new Timer(_ => OnWatchdogTick(), null, period, period);
    }

    private void OnWatchdogTick()
    {
        try
        {
            if (!IsStalledAt(DateTimeOffset.UtcNow))
            {
                return;
            }

            _log.LogError("Watchdog: no progress for {Stall}s in state {State}; failing execution.",
                _settings.StallDetectionSeconds, _machine.Current);
            _events.Publish(Domain.Events.AgentEvent.Create(Domain.Enums.AgentEventType.AgentStopped, _correlation,
                $"Watchdog: stalled in {_machine.Current}, failing."));

            try
            {
                if (!global::CaAIA.Agent.StateMachine.AgentTransitions.Terminal.Contains(_machine.Current))
                {
                    _machine.TransitionTo(Domain.Enums.AgentState.Failed, "watchdog: stalled operation");
                    // Marcar también el plan: antes quedaba en Executing/Auditing con el
                    // motor en Failed (divergencia visible en la pestaña Plan).
                    var plan = _activePlan;
                    if (plan is not null)
                    {
                        _ = FailActivePlanAsync(plan);
                    }

                    _ = CheckpointAsync("watchdog stall");
                }
            }
            catch (InvalidOperationException ex)
            {
                _log.LogDebug(ex, "Watchdog transition rejected");
            }

            _internalCts.Cancel();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Watchdog tick failed");
        }
    }

    private async Task FailActivePlanAsync(Plan plan)
    {
        try
        {
            plan.MarkFailed();
            await _plans.SaveAsync(plan, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Watchdog could not persist failed plan");
        }
    }

    private void StopWatchdog()
    {
        try
        {
            _watchdog?.Dispose();
        }
        catch (Exception)
        {
        }

        _watchdog = null;
    }

    public ValueTask DisposeAsync()
    {
        StopHeartbeat();
        StopWatchdog();
        _internalCts.Dispose();
        _runGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
