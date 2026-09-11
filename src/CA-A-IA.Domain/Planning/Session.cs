using System.Text.Json.Serialization;
using CaAIA.Domain.Enums;

namespace CaAIA.Domain.Planning;

/// <summary>
/// Sesión de trabajo recuperable (§7): sobrevive al cierre de la app y al reinicio de Windows
/// mediante <see cref="Persistence.ICheckpointStore"/> + <see cref="Persistence.IAgentSessionStore"/>.
/// </summary>
public sealed class AgentSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string WorkspacePath { get; init; }
    public required string UserRequest { get; init; }
    [JsonInclude] public Guid? PlanId { get; private set; }
    [JsonInclude] public AgentState LastKnownState { get; private set; } = AgentState.Idle;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonInclude] public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;
    [JsonInclude] public DateTimeOffset? HeartbeatAt { get; private set; }
    [JsonInclude] public bool IsActive { get; private set; } = true;

    public void AttachPlan(Guid planId)
    {
        PlanId = planId;
        Touch();
    }

    public void RecordState(AgentState state)
    {
        LastKnownState = state;
        Touch();
    }

    public void Heartbeat()
    {
        HeartbeatAt = DateTimeOffset.UtcNow;
    }

    public void Close()
    {
        IsActive = false;
        Touch();
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

/// <summary>
/// Checkpoint inmutable del progreso: plan + tarea actual + estado. Base de la reanudación.
/// </summary>
public sealed record Checkpoint(
    Guid Id,
    Guid SessionId,
    Guid? PlanId,
    AgentState State,
    Guid? CurrentTaskId,
    int PlanRevision,
    DateTimeOffset CreatedAt,
    string? Note = null);

/// <summary>Resultado de la auditoría final del plan (§9).</summary>
public sealed record VerificationReport(
    Guid PlanId,
    int PlanRevision,
    bool IsSatisfied,
    IReadOnlyList<string> MissingRequirements,
    IReadOnlyList<string> Evidence,
    DateTimeOffset VerifiedAt);
