using System.Text.Json.Serialization;
using CaAIA.Domain.Enums;

namespace CaAIA.Domain.Planning;

/// <summary>
/// Tarea individual del plan. Conoce sus dependencias por Id, sus intentos y su verificación.
/// Las transiciones de estado las gobierna el motor del agente; esta entidad solo valida invariantes.
/// </summary>
public sealed class AgentTask
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;

    /// <summary>Ids de tareas que deben estar <see cref="AgentTaskStatus.Completed"/> antes de empezar.</summary>
    public IReadOnlyList<Guid> DependsOn { get; init; } = Array.Empty<Guid>();

    [JsonInclude] public AgentTaskStatus Status { get; private set; } = AgentTaskStatus.Pending;
    [JsonInclude] public int Attempts { get; private set; }
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Criterios de aceptación verificables de ESTA tarea.</summary>
    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = Array.Empty<string>();

    [JsonInclude] public DateTimeOffset? StartedAt { get; private set; }
    [JsonInclude] public DateTimeOffset? FinishedAt { get; private set; }
    [JsonInclude] public string? LastError { get; private set; }
    [JsonInclude] public FailureCategory? LastFailureCategory { get; private set; }

    public bool CanRetry => Attempts < MaxAttempts;

    /// <summary>
    /// ¿Puede el planificador elegirla ahora? Solo <c>Pending</c> con dependencias completadas.
    /// Las tareas <c>Failed</c> NO se re-ejecutan solas: solo el repair loop (mismo intento lógico)
    /// puede reiniciarlas; si el repair desiste, quedan <c>Failed</c> y el plan escala a fallo.
    /// </summary>
    public bool IsReady(IReadOnlyDictionary<Guid, AgentTaskStatus> statuses)
    {
        if (Status != AgentTaskStatus.Pending)
        {
            return false;
        }

        foreach (var dep in DependsOn)
        {
            if (!statuses.TryGetValue(dep, out var s) || s != AgentTaskStatus.Completed)
            {
                return false;
            }
        }

        return true;
    }

    public void MarkStarted()
    {
        if (Status is AgentTaskStatus.Completed or AgentTaskStatus.Skipped)
        {
            throw new InvalidOperationException($"Task '{Title}' is already closed ({Status}).");
        }

        Status = AgentTaskStatus.InProgress;
        StartedAt = DateTimeOffset.UtcNow;
        Attempts++;
    }

    public void MarkCompleted()
    {
        if (Status != AgentTaskStatus.InProgress)
        {
            throw new InvalidOperationException($"Only an InProgress task can complete (task '{Title}' is {Status}).");
        }

        Status = AgentTaskStatus.Completed;
        FinishedAt = DateTimeOffset.UtcNow;
        LastError = null;
        LastFailureCategory = null;
    }

    public void MarkFailed(string error, FailureCategory category)
    {
        if (Status != AgentTaskStatus.InProgress)
        {
            throw new InvalidOperationException($"Only an InProgress task can fail (task '{Title}' is {Status}).");
        }

        Status = AgentTaskStatus.Failed;
        FinishedAt = DateTimeOffset.UtcNow;
        LastError = error;
        LastFailureCategory = category;
    }

    public void MarkBlocked(string reason)
    {
        Status = AgentTaskStatus.Blocked;
        LastError = reason;
    }

    public void MarkSkipped(string reason)
    {
        if (Status is AgentTaskStatus.Completed)
        {
            throw new InvalidOperationException($"Completed task '{Title}' cannot be skipped.");
        }

        Status = AgentTaskStatus.Skipped;
        FinishedAt = DateTimeOffset.UtcNow;
        LastError = reason;
    }

    public void MarkNeedsReview(string reason)
    {
        Status = AgentTaskStatus.NeedsReview;
        LastError = reason;
    }
}
