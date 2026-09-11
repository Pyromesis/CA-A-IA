// CA-A-IA — Contrato del ejecutor de tarea + resultado clasificado.

using CaAIA.Domain.Enums;
using CaAIA.Domain.Planning;

namespace CaAIA.Agent.Execution;

/// <summary>
/// Ejecuta UNA tarea (IA + herramientas + build + tests). El motor (<c>AgentExecutionEngine</c>)
/// orquesta estados, reintentos y verificación; esta interfaz es el punto de extensión.
/// Implementación real: <see cref="LlmTaskExecutor"/>.
/// </summary>
public interface ITaskExecutor
{
    Task<TaskExecutionOutcome> ExecuteTaskAsync(Plan plan, AgentTask task, CancellationToken cancellationToken);
}

/// <summary>Resultado con categoría de fallo (§20) para decidir reparar vs escalar.</summary>
public sealed record TaskExecutionOutcome(bool Success, string? Error = null, FailureCategory Category = FailureCategory.Unknown);
