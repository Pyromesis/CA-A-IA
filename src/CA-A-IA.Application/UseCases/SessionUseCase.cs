// CA-A-IA · Fase 0 — Caso de uso: ciclo de vida de sesiones (crear / reanudar / pausar / cancelar).

using CaAIA.Application.Commands;
using CaAIA.Application.DTOs;
using CaAIA.Application.Queries;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Events;
using CaAIA.Domain.Execution;
using CaAIA.Domain.Persistence;
using CaAIA.Domain.Planning;
using Microsoft.Extensions.Logging;

namespace CaAIA.Application.UseCases;

/// <summary>
/// Orquesta sesiones contra los stores + máquina de estados. No conoce WinUI ni proveedores concretos.
/// </summary>
public sealed class SessionUseCase :
    ICommandHandler<CreateSessionCommand, SessionDto>,
    ICommandHandler<PauseSessionCommand, SessionDto>,
    ICommandHandler<ResumeSessionCommand, SessionDto>,
    ICommandHandler<CancelSessionCommand, SessionDto>,
    IQueryHandler<GetSessionQuery, SessionDto?>,
    IQueryHandler<ListActiveSessionsQuery, IReadOnlyList<SessionDto>>
{
    private readonly IAgentSessionStore _sessions;
    private readonly ICheckpointStore _checkpoints;
    private readonly IEventBus _events;
    private readonly ILogger<SessionUseCase> _log;

    public SessionUseCase(
        IAgentSessionStore sessions,
        ICheckpointStore checkpoints,
        IEventBus events,
        ILogger<SessionUseCase> log)
    {
        _sessions = sessions;
        _checkpoints = checkpoints;
        _events = events;
        _log = log;
    }

    public async Task<SessionDto> HandleAsync(CreateSessionCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.WorkspacePath))
        {
            throw new ArgumentException("Workspace path is required.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.UserRequest))
        {
            throw new ArgumentException("User request is required.", nameof(command));
        }

        var session = new AgentSession
        {
            WorkspacePath = command.WorkspacePath,
            UserRequest = command.UserRequest,
        };

        await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        await _checkpoints.AppendExecutionLogAsync(
            new ExecutionLogEntry(Guid.NewGuid(), session.Id, DateTimeOffset.UtcNow,
                "Session", "Session created.", AgentState.Idle),
            cancellationToken).ConfigureAwait(false);

        _events.Publish(AgentEvent.Create(
            AgentEventType.AgentStarted, null, $"Session {session.Id} created."));
        _log.LogInformation("Session {SessionId} created for workspace {Workspace}",
            session.Id, session.WorkspacePath);

        return session.ToDto();
    }

    public async Task<SessionDto> HandleAsync(PauseSessionCommand command, CancellationToken cancellationToken)
    {
        var session = await RequireAsync(command.SessionId, cancellationToken).ConfigureAwait(false);
        session.RecordState(AgentState.Paused);
        await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        _events.Publish(AgentEvent.Create(AgentEventType.AgentPaused, null, $"Session {session.Id} paused."));
        return session.ToDto();
    }

    public async Task<SessionDto> HandleAsync(ResumeSessionCommand command, CancellationToken cancellationToken)
    {
        var session = await RequireAsync(command.SessionId, cancellationToken).ConfigureAwait(false);
        var latest = await _checkpoints.LoadLatestAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var resumeFrom = latest?.State ?? AgentState.Idle;
        session.RecordState(resumeFrom);
        await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        _events.Publish(AgentEvent.Create(
            AgentEventType.SessionRecovered, null, $"Session {session.Id} resumed from {resumeFrom}."));
        return session.ToDto();
    }

    public async Task<SessionDto> HandleAsync(CancelSessionCommand command, CancellationToken cancellationToken)
    {
        var session = await RequireAsync(command.SessionId, cancellationToken).ConfigureAwait(false);
        session.RecordState(AgentState.Cancelled);
        session.Close();
        await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        _events.Publish(AgentEvent.Create(AgentEventType.AgentStopped, null, $"Session {session.Id} cancelled."));
        return session.ToDto();
    }

    public async Task<SessionDto?> HandleAsync(GetSessionQuery query, CancellationToken cancellationToken)
    {
        var session = await _sessions.LoadAsync(query.SessionId, cancellationToken).ConfigureAwait(false);
        return session?.ToDto();
    }

    public async Task<IReadOnlyList<SessionDto>> HandleAsync(
        ListActiveSessionsQuery query, CancellationToken cancellationToken)
    {
        var sessions = await _sessions.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        return sessions.Select(s => s.ToDto()).ToList();
    }

    private async Task<AgentSession> RequireAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            throw new KeyNotFoundException($"Session {sessionId} not found.");
        }

        return session;
    }
}
