// CA-A-IA · Fase 0 — Comandos y queries (CQRS ligero, sin mediador externo).

using CaAIA.Application.DTOs;

namespace CaAIA.Application.Commands;

/// <summary>Comando con resultado tipado.</summary>
public interface ICommand<TResult>;

/// <summary>Manejador de un comando. Registrados en DI (AddApplication).</summary>
public interface ICommandHandler<TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public sealed record CreateSessionCommand(string WorkspacePath, string UserRequest) : ICommand<SessionDto>;
public sealed record AnswerClarificationCommand(Guid PlanId, Guid QuestionId, string Answer) : ICommand<PlanDto>;
public sealed record ApprovePlanCommand(Guid PlanId) : ICommand<PlanDto>;
public sealed record PauseSessionCommand(Guid SessionId) : ICommand<SessionDto>;
public sealed record ResumeSessionCommand(Guid SessionId) : ICommand<SessionDto>;
public sealed record CancelSessionCommand(Guid SessionId) : ICommand<SessionDto>;
