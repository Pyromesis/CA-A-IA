using CaAIA.Domain.Enums;
using CaAIA.Domain.Planning;

namespace CaAIA.Domain.Persistence;

/// <summary>Persistencia de planes (transaccional cuando corresponda, versionable). Impl. en Infrastructure.</summary>
public interface IPlanStore
{
    Task SaveAsync(Plan plan, CancellationToken cancellationToken);
    Task<Plan?> LoadAsync(Guid planId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Plan>> ListBySessionAsync(Guid sessionId, CancellationToken cancellationToken);
}

/// <summary>Persistencia de sesiones de trabajo (recuperación tras cierre/reinicio, §7).</summary>
public interface IAgentSessionStore
{
    Task SaveAsync(AgentSession session, CancellationToken cancellationToken);
    Task<AgentSession?> LoadAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentSession>> ListActiveAsync(CancellationToken cancellationToken);
    Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken);
}

/// <summary>Checkpoints de plan/tarea/estado + log de ejecución (append-only).</summary>
public interface ICheckpointStore
{
    Task SaveCheckpointAsync(Checkpoint checkpoint, CancellationToken cancellationToken);
    Task<Checkpoint?> LoadLatestAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Checkpoint>> ListBySessionAsync(Guid sessionId, CancellationToken cancellationToken);
    Task AppendExecutionLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<ExecutionLogEntry>> ReadExecutionLogAsync(Guid sessionId, CancellationToken cancellationToken);
}

/// <summary>Entrada append-only del registro de actividad (observabilidad §26).</summary>
public sealed record ExecutionLogEntry(
    Guid Id,
    Guid SessionId,
    DateTimeOffset OccurredAt,
    string Category,
    string Message,
    AgentState? State = null,
    Guid? TaskId = null,
    string? DetailsJson = null);
