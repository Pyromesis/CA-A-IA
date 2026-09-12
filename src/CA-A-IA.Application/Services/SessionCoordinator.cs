using CaAIA.Application.Commands;
using CaAIA.Application.Configuration;
using CaAIA.Application.DTOs;
using CaAIA.Application.Queries;
using CaAIA.Application.UseCases;
using CaAIA.Domain.Context;
using CaAIA.Domain.Execution;
using CaAIA.Domain.Git;
using CaAIA.Domain.Persistence;
using Microsoft.Extensions.Options;

namespace CaAIA.Application.Services;

/// <summary>Implementación por defecto: delega en casos de uso + motor del agente.</summary>
public sealed class SessionCoordinator : ISessionCoordinator
{
    private readonly SessionUseCase _sessions;
    private readonly PlanningUseCase _planning;
    private readonly IAgentEngineFactory _engines;
    private readonly IPlanner _planner;
    private readonly IPlanStore _plans;
    private readonly IAgentSessionStore _sessionsStore;
    private readonly IContextBuilder _context;
    private readonly IGitService _git;
    private readonly Domain.Events.IEventBus _events;
    private readonly IOptions<CaAIAOptions> _options;

    public SessionCoordinator(
        SessionUseCase sessions,
        PlanningUseCase planning,
        IAgentEngineFactory engines,
        IPlanner planner,
        IPlanStore plans,
        IAgentSessionStore sessionsStore,
        IContextBuilder context,
        IGitService git,
        Domain.Events.IEventBus events,
        IOptions<CaAIAOptions> options)
    {
        _sessions = sessions;
        _planning = planning;
        _engines = engines;
        _planner = planner;
        _plans = plans;
        _sessionsStore = sessionsStore;
        _context = context;
        _git = git;
        _events = events;
        _options = options;
    }

    public Task<SessionDto> StartSessionAsync(string workspacePath, string userRequest, CancellationToken cancellationToken) =>
        _sessions.HandleAsync(new CreateSessionCommand(workspacePath, userRequest), cancellationToken);

    /// <summary>
    /// Genera el plan con el planner (modelo o heurístico), lo persiste y lo adjunta a la sesión.
    /// </summary>
    public async Task<PlanDto> CreatePlanAsync(Guid sessionId, string userRequest, CancellationToken cancellationToken)
    {
        var session = await _sessionsStore.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session {sessionId} not found.");

        IReadOnlyList<string> files;
        try
        {
            var context = await _context.BuildAsync(session.WorkspacePath, new[] { userRequest },
                Math.Max(5000, _options.Value.Workspace.MaxContextChars / 3), cancellationToken)
                .ConfigureAwait(false);
            files = context.Fragments.Select(f => f.Source).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            files = Array.Empty<string>();
        }

        string? gitSummary = null;
        try
        {
            var status = await _git.GetStatusAsync(session.WorkspacePath, cancellationToken).ConfigureAwait(false);
            gitSummary = $"{status.Branch ?? "?"} {(status.HasChanges ? "dirty" : "clean")}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
        }

        var plan = await _planner.CreatePlanAsync(sessionId, userRequest,
            new ProjectContextSnapshot(session.WorkspacePath, files, null, gitSummary), cancellationToken)
            .ConfigureAwait(false);
        await _plans.SaveAsync(plan, cancellationToken).ConfigureAwait(false);
        session.AttachPlan(plan.Id);
        await _sessionsStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        _events.Publish(new Domain.Events.AgentEvent(Guid.NewGuid(),
            Domain.Enums.AgentEventType.PlanCreated, DateTimeOffset.UtcNow, null,
            $"Plan {plan.Id:N} rev {plan.Revision}: {plan.Tasks.Count} tareas."));
        return plan.ToDto();
    }

    public Task<PlanDto?> GetPlanAsync(Guid sessionId, CancellationToken cancellationToken) =>
        _planning.HandleAsync(new GetSessionPlanQuery(sessionId), cancellationToken);

    public Task<PlanDto> ApprovePlanAsync(Guid planId, CancellationToken cancellationToken) =>
        _planning.HandleAsync(new ApprovePlanCommand(planId), cancellationToken);

    public Task<PlanDto> AnswerQuestionAsync(Guid planId, Guid questionId, string answer, CancellationToken cancellationToken) =>
        _planning.HandleAsync(new AnswerClarificationCommand(planId, questionId, answer), cancellationToken);

    public async Task RunAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var engine = _engines.GetOrCreate(sessionId);
        await engine.RunAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var engine = _engines.GetOrCreate(sessionId);
        await engine.PauseAsync(cancellationToken).ConfigureAwait(false);
        await _sessions.HandleAsync(new PauseSessionCommand(sessionId), cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await _sessions.HandleAsync(new ResumeSessionCommand(sessionId), cancellationToken).ConfigureAwait(false);
        // Motor nuevo: el anterior consumió su CTS interno en PauseAsync y cualquier
        // Run posterior saldría cancelado de inmediato.
        var engine = _engines.Recreate(sessionId);
        await engine.ResumeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> NudgeAsync(Guid sessionId, string instruction, CancellationToken cancellationToken)
    {
        var session = await _sessionsStore.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session {sessionId} not found.");
        if (session.PlanId is null)
        {
            throw new InvalidOperationException("Session has no plan to continue.");
        }

        var plan = await _plans.LoadAsync(session.PlanId.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Plan {session.PlanId} not found.");
        var requeued = plan.RequeueInflight(instruction);
        await _plans.SaveAsync(plan, cancellationToken).ConfigureAwait(false);

        // Motor fresco (el anterior puede estar a medias o con el CTS consumido):
        // se descarta sin duelo y el llamador continúa con RunAsync.
        try
        {
            var engine = _engines.GetOrCreate(sessionId);
            var state = engine.StateMachine.Current;
            if (state is not Domain.Enums.AgentState.Completed
                and not Domain.Enums.AgentState.Failed
                and not Domain.Enums.AgentState.Cancelled
                and not Domain.Enums.AgentState.Idle)
            {
                await engine.CancelAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Best-effort: lo importante (plan reencolado) ya está guardado.
        }

        _engines.Remove(sessionId);
        return requeued;
    }

    public async Task CancelAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var engine = _engines.GetOrCreate(sessionId);
        await engine.CancelAsync(cancellationToken).ConfigureAwait(false);
        await _sessions.HandleAsync(new CancelSessionCommand(sessionId), cancellationToken).ConfigureAwait(false);
        _engines.Remove(sessionId);
    }

    public Task<IReadOnlyList<SessionDto>> ListActiveSessionsAsync(CancellationToken cancellationToken) =>
        _sessions.HandleAsync(new ListActiveSessionsQuery(), cancellationToken);
}
