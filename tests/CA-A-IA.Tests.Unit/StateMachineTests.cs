// CA-A-IA · Fase 0 — Tests de la máquina de estados: tabla, reglas globales e historial.

using CaAIA.Agent.StateMachine;
using CaAIA.Domain.Enums;

namespace CaAIA.Tests.Unit;

public sealed class StateMachineTests
{
    [Fact]
    public void InitialState_IsIdle()
    {
        var machine = new AgentStateMachine();
        Assert.Equal(AgentState.Idle, machine.Current);
    }

    [Fact]
    public void HappyPath_FullLifecycle_WalksWithoutThrowing()
    {
        var machine = new AgentStateMachine();
        AgentState[] path =
        [
            AgentState.Understanding, AgentState.InspectingProject, AgentState.Planning,
            AgentState.PlanReview, AgentState.PreparingExecution, AgentState.Executing,
            AgentState.Testing, AgentState.VerifyingTask, AgentState.AdvancingTask,
            AgentState.FinalVerification, AgentState.Completed, AgentState.Idle,
        ];
        foreach (var next in path)
        {
            Assert.True(machine.CanTransitionTo(next), $"Expected {machine.Current} -> {next} to be legal");
            machine.TransitionTo(next, "test");
        }

        Assert.Equal(AgentState.Idle, machine.Current);
        Assert.Equal(path.Length, machine.History.Count);
    }

    [Fact]
    public void RepairBranch_IsReachable()
    {
        var machine = new AgentStateMachine(AgentState.Executing);
        machine.TransitionTo(AgentState.AnalyzingFailure);
        machine.TransitionTo(AgentState.Repairing);
        machine.TransitionTo(AgentState.Retesting);
        machine.TransitionTo(AgentState.VerifyingTask);
        Assert.Equal(AgentState.VerifyingTask, machine.Current);
    }

    [Theory]
    [InlineData(AgentState.Idle, AgentState.Executing)]
    [InlineData(AgentState.Planning, AgentState.Executing)]
    [InlineData(AgentState.Completed, AgentState.Executing)]
    [InlineData(AgentState.Executing, AgentState.Planning)]
    [InlineData(AgentState.Idle, AgentState.Unknown)]
    public void IllegalTransition_Throws(AgentState from, AgentState to)
    {
        var machine = new AgentStateMachine(from);
        Assert.False(machine.CanTransitionTo(to));
        Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(to));
        Assert.Equal(from, machine.Current); // sin cambios tras el intento
    }

    [Theory]
    [InlineData(AgentState.Understanding)]
    [InlineData(AgentState.Executing)]
    [InlineData(AgentState.FinalVerification)]
    public void GlobalRule_NonTerminal_CanAlwaysPauseOrCancel(AgentState from)
    {
        var machine = new AgentStateMachine(from);
        Assert.Contains(AgentState.Paused, machine.AllowedTransitions);
        Assert.Contains(AgentState.Cancelled, machine.AllowedTransitions);
    }

    [Theory]
    [InlineData(AgentState.Completed)]
    [InlineData(AgentState.Cancelled)]
    [InlineData(AgentState.Failed)]
    public void TerminalStates_CannotPause(AgentState terminal)
    {
        var machine = new AgentStateMachine(terminal);
        Assert.DoesNotContain(AgentState.Paused, machine.AllowedTransitions);
    }

    [Fact]
    public void Restore_HydratesStateAndHistory()
    {
        var history = new[] { new Domain.Execution.StateTransition(AgentState.Idle, AgentState.Paused, DateTimeOffset.UtcNow) };
        var machine = AgentStateMachine.Restore(AgentState.Paused, history);
        Assert.Equal(AgentState.Paused, machine.Current);
        Assert.Single(machine.History);
        machine.TransitionTo(AgentState.Recovering);
        Assert.Equal(AgentState.Recovering, machine.Current);
    }

    [Fact]
    public void Transition_RaisesEvent_WithFromTo()
    {
        var machine = new AgentStateMachine();
        Domain.Execution.StateTransition? observed = null;
        machine.Transitioned += (_, t) => observed = t;
        machine.TransitionTo(AgentState.Understanding, "test");
        Assert.NotNull(observed);
        Assert.Equal(AgentState.Idle, observed.From);
        Assert.Equal(AgentState.Understanding, observed.To);
    }
}
