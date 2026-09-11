using CaAIA.Domain.Enums;
using CaAIA.Domain.Planning;

namespace CaAIA.Domain.Execution;

/// <summary>Transición observada de la máquina de estados (para UI, logs e historial).</summary>
public sealed record StateTransition(
    AgentState From,
    AgentState To,
    DateTimeOffset OccurredAt,
    string? Reason = null);

/// <summary>
/// Máquina de estados explícita del agente (§6). Tabla de transiciones declarativa,
/// persistible vía <see cref="Persistence.ICheckpointStore"/>. Sin `if/else` gigantes.
/// </summary>
public interface IAgentStateMachine
{
    AgentState Current { get; }
    event EventHandler<StateTransition>? Transitioned;
    bool CanTransitionTo(AgentState next);
    void TransitionTo(AgentState next, string? reason = null);
    IReadOnlyList<AgentState> AllowedTransitions { get; }
}

/// <summary>
/// Motor de ejecución (§18): ejecuta tareas/herramientas, maneja estados, cancellation,
/// checkpoints y verificación. Desacoplado de WinUI. Implementación: AgentExecutionEngine;
/// ejecución por tarea: ITaskExecutor (LlmTaskExecutor).
/// </summary>
public interface IAgentExecutionEngine
{
    Guid ExecutionId { get; }
    IAgentStateMachine StateMachine { get; }
    Task RunAsync(Guid sessionId, CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task CancelAsync(CancellationToken cancellationToken);
}

/// <summary>Construye el <see cref="Plan"/> (fases Understand→Plan). Motor real en fase posterior.</summary>
public interface IPlanner
{
    Task<Plan> CreatePlanAsync(
        Guid sessionId, string userRequest, ProjectContextSnapshot project, CancellationToken cancellationToken);
}

/// <summary>Instantánea mínima del proyecto para planificar (el contexto rico vive en <c>IContextBuilder</c>).</summary>
public sealed record ProjectContextSnapshot(
    string WorkspacePath,
    IReadOnlyList<string> RelevantFiles,
    string? BuildSummary,
    string? GitSummary);

/// <summary>
/// Auditoría final del plan (§9): request vs plan vs implementación vs tests.
/// Puede exigir tareas adicionales (<see cref="Plan.ExtendAfterAudit"/>).
/// </summary>
public interface IPlanVerifier
{
    Task<VerificationReport> AuditAsync(
        string userRequest, Plan plan, IReadOnlyList<string> testEvidence, CancellationToken cancellationToken);
}

/// <summary>Política de reparación tras fallos (testing loop §19 + categorías §20).</summary>
public interface IRepairPolicy
{
    bool ShouldRepair(FailureCategory category, int attempts, int maxAttempts);
    TimeSpan DelayBeforeRetry(FailureCategory category, int attempts);
}
