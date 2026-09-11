// CA-A-IA · Fase 0 — Máquina de estados thread-safe con historial.

using CaAIA.Domain.Enums;
using CaAIA.Domain.Execution;

namespace CaAIA.Agent.StateMachine;

/// <summary>
/// Implementación de <see cref="IAgentStateMachine"/> sobre <see cref="AgentTransitions"/>.
/// Persistible: expone <c>Current</c> + <c>History</c> para hidratarse desde un checkpoint.
/// </summary>
public sealed class AgentStateMachine : IAgentStateMachine
{
    private readonly object _gate = new();
    private readonly List<StateTransition> _history = new();

    public AgentState Current { get; private set; }
    public event EventHandler<StateTransition>? Transitioned;

    public IReadOnlyList<AgentState> AllowedTransitions
    {
        get
        {
            lock (_gate)
            {
                return AgentTransitions.AllowedFrom(Current);
            }
        }
    }

    /// <summary>Historial de transiciones (para logs, UI y auditoría).</summary>
    public IReadOnlyList<StateTransition> History
    {
        get
        {
            lock (_gate)
            {
                return _history.ToList();
            }
        }
    }

    public AgentStateMachine(AgentState initial = AgentState.Idle)
    {
        if (initial is AgentState.Unknown)
        {
            throw new ArgumentException("Initial state cannot be Unknown.", nameof(initial));
        }

        Current = initial;
    }

    /// <summary>Hidrata la máquina desde un estado persistido (recuperación, §7).</summary>
    public static AgentStateMachine Restore(AgentState persisted, IEnumerable<StateTransition>? history = null)
    {
        var machine = new AgentStateMachine(persisted);
        if (history is not null)
        {
            machine._history.AddRange(history);
        }

        return machine;
    }

    public bool CanTransitionTo(AgentState next)
    {
        lock (_gate)
        {
            return AgentTransitions.IsAllowed(Current, next);
        }
    }

    public void TransitionTo(AgentState next, string? reason = null)
    {
        StateTransition transition;
        EventHandler<StateTransition>? handlers;
        lock (_gate)
        {
            if (!AgentTransitions.IsAllowed(Current, next))
            {
                throw new InvalidOperationException(
                    $"Illegal agent transition: {Current} -> {next}." +
                    (reason is null ? string.Empty : $" Reason: {reason}"));
            }

            transition = new StateTransition(Current, next, DateTimeOffset.UtcNow, reason);
            Current = next;
            _history.Add(transition);
            handlers = Transitioned;
        }

        // Notificar fuera del lock: los handlers pueden consultar la máquina.
        handlers?.Invoke(this, transition);
    }
}
