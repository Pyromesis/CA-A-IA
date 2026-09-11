// CA-A-IA · Fase 0 — Caso de uso: planificación (crear / responder aclaraciones / aprobar / auditar).

using CaAIA.Application.Commands;
using CaAIA.Application.DTOs;
using CaAIA.Application.Queries;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Events;
using CaAIA.Domain.Execution;
using CaAIA.Domain.Persistence;
using Microsoft.Extensions.Logging;

namespace CaAIA.Application.UseCases;

/// <summary>
/// Flujo Understand→Plan→Review. La generación real del plan la hará <see cref="IPlanner"/>
/// (Agent); aquí viven las reglas de aplicación (invariantes + eventos + persistencia).
/// </summary>
public sealed class PlanningUseCase :
    ICommandHandler<AnswerClarificationCommand, PlanDto>,
    ICommandHandler<ApprovePlanCommand, PlanDto>,
    IQueryHandler<GetPlanQuery, PlanDto?>,
    IQueryHandler<GetSessionPlanQuery, PlanDto?>
{
    private readonly IPlanStore _plans;
    private readonly IAgentSessionStore _sessions;
    private readonly IEventBus _events;
    private readonly ILogger<PlanningUseCase> _log;

    public PlanningUseCase(
        IPlanStore plans,
        IAgentSessionStore sessions,
        IEventBus events,
        ILogger<PlanningUseCase> log)
    {
        _plans = plans;
        _sessions = sessions;
        _events = events;
        _log = log;
    }

    public async Task<PlanDto> HandleAsync(AnswerClarificationCommand command, CancellationToken cancellationToken)
    {
        var plan = await RequirePlanAsync(command.PlanId, cancellationToken).ConfigureAwait(false);
        var question = plan.OpenQuestions.FirstOrDefault(q => q.Id == command.QuestionId)
            ?? throw new KeyNotFoundException($"Question {command.QuestionId} not found in plan {plan.Id}.");

        question.AnswerQuestion(command.Answer);
        await _plans.SaveAsync(plan, cancellationToken).ConfigureAwait(false);
        _log.LogInformation("Question {QuestionId} answered in plan {PlanId}", question.Id, plan.Id);
        return plan.ToDto();
    }

    public async Task<PlanDto> HandleAsync(ApprovePlanCommand command, CancellationToken cancellationToken)
    {
        var plan = await RequirePlanAsync(command.PlanId, cancellationToken).ConfigureAwait(false);
        plan.MarkApproved();
        plan.MarkExecuting();
        await _plans.SaveAsync(plan, cancellationToken).ConfigureAwait(false);

        var session = await _sessions.LoadAsync(plan.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is not null)
        {
            session.RecordState(AgentState.Executing);
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        }

        _events.Publish(AgentEvent.Create(AgentEventType.PlanCreated, null, $"Plan {plan.Id} approved."));
        return plan.ToDto();
    }

    public async Task<PlanDto?> HandleAsync(GetPlanQuery query, CancellationToken cancellationToken)
    {
        var plan = await _plans.LoadAsync(query.PlanId, cancellationToken).ConfigureAwait(false);
        return plan?.ToDto();
    }

    public async Task<PlanDto?> HandleAsync(GetSessionPlanQuery query, CancellationToken cancellationToken)
    {
        var session = await _sessions.LoadAsync(query.SessionId, cancellationToken).ConfigureAwait(false);
        if (session?.PlanId is null)
        {
            return null;
        }

        var plan = await _plans.LoadAsync(session.PlanId.Value, cancellationToken).ConfigureAwait(false);
        return plan?.ToDto();
    }

    private async Task<Domain.Planning.Plan> RequirePlanAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan = await _plans.LoadAsync(planId, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            throw new KeyNotFoundException($"Plan {planId} not found.");
        }

        return plan;
    }
}
