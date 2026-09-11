// CA-A-IA · Fase 0 — Tabla declarativa de transiciones (§6). Única fuente de verdad del flujo.

using CaAIA.Domain.Enums;

namespace CaAIA.Agent.StateMachine;

/// <summary>
/// Transiciones legales de la máquina de estados. Ninguna otra transición es válida;
/// <see cref="AgentStateMachine"/> la aplica sin `if/else` de negocio.
/// Regla global: desde cualquier estado NO terminal se puede ir a <c>Paused</c>/<c>Cancelled</c>.
/// </summary>
public static class AgentTransitions
{
    public static readonly IReadOnlySet<AgentState> Terminal =
        new HashSet<AgentState> { AgentState.Completed, AgentState.Cancelled, AgentState.Failed };

    private static readonly IReadOnlyDictionary<AgentState, IReadOnlySet<AgentState>> Table =
        new Dictionary<AgentState, IReadOnlySet<AgentState>>
        {
            [AgentState.Idle] = S(AgentState.Understanding, AgentState.Recovering, AgentState.PreparingExecution),
            [AgentState.Understanding] = S(AgentState.InspectingProject, AgentState.ClarificationRequired, AgentState.Failed),
            [AgentState.InspectingProject] = S(AgentState.ClarificationRequired, AgentState.Planning, AgentState.Failed),
            [AgentState.ClarificationRequired] = S(AgentState.Understanding),
            [AgentState.Planning] = S(AgentState.PlanReview, AgentState.Failed),
            [AgentState.PlanReview] = S(AgentState.Planning, AgentState.PreparingExecution),
            [AgentState.PreparingExecution] = S(AgentState.Executing, AgentState.Failed),
            [AgentState.Executing] = S(AgentState.Testing, AgentState.VerifyingTask, AgentState.AnalyzingFailure, AgentState.Failed),
            [AgentState.Testing] = S(AgentState.VerifyingTask, AgentState.AnalyzingFailure, AgentState.Failed),
            [AgentState.AnalyzingFailure] = S(AgentState.Repairing, AgentState.AdvancingTask, AgentState.Failed),
            [AgentState.Repairing] = S(AgentState.Retesting, AgentState.Failed),
            [AgentState.Retesting] = S(AgentState.VerifyingTask, AgentState.AnalyzingFailure, AgentState.Failed),
            [AgentState.VerifyingTask] = S(AgentState.AdvancingTask, AgentState.AnalyzingFailure, AgentState.Failed),
            [AgentState.AdvancingTask] = S(AgentState.Executing, AgentState.FinalVerification, AgentState.Failed),
            [AgentState.FinalVerification] = S(AgentState.Completed, AgentState.Executing, AgentState.AdvancingTask, AgentState.Failed),
            [AgentState.Completed] = S(AgentState.Idle),
            [AgentState.Paused] = S(AgentState.Recovering, AgentState.Cancelled, AgentState.Idle),
            [AgentState.Cancelled] = S(AgentState.Idle),
            [AgentState.Failed] = S(AgentState.Recovering, AgentState.Idle),
            [AgentState.Recovering] = S(AgentState.PreparingExecution, AgentState.Understanding, AgentState.Idle, AgentState.Failed),
        };

    public static bool IsAllowed(AgentState from, AgentState to)
    {
        if (to is AgentState.Unknown)
        {
            return false;
        }

        if (Table.TryGetValue(from, out var allowed) && allowed.Contains(to))
        {
            return true;
        }

        // Regla global de cancelación/pausa cooperativa.
        if (!Terminal.Contains(from) && to is AgentState.Paused or AgentState.Cancelled)
        {
            return true;
        }

        return false;
    }

    public static IReadOnlyList<AgentState> AllowedFrom(AgentState from)
    {
        var result = new HashSet<AgentState>(
            Table.TryGetValue(from, out var allowed) ? allowed : Enumerable.Empty<AgentState>());
        if (!Terminal.Contains(from))
        {
            result.Add(AgentState.Paused);
            result.Add(AgentState.Cancelled);
        }

        return result.OrderBy(s => s).ToList();
    }

    private static HashSet<AgentState> S(params AgentState[] states) => new(states);
}
