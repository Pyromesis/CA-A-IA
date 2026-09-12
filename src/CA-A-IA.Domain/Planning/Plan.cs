using System.Text.Json.Serialization;
using CaAIA.Domain.Enums;

namespace CaAIA.Domain.Planning;

/// <summary>Plan de trabajo del agente (§8). Raíz del grafo de tareas + auditoría final (§9).</summary>
public sealed class Plan
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public required string Goal { get; init; }
    public IReadOnlyList<string> Requirements { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Assumptions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Constraints { get; init; } = Array.Empty<string>();
    [JsonInclude] public IReadOnlyList<Clarification> OpenQuestions { get; private set; } = Array.Empty<Clarification>();
    public IReadOnlyList<PlanRisk> Risks { get; init; } = Array.Empty<PlanRisk>();
    public IReadOnlyList<string> FinalAcceptanceCriteria { get; init; } = Array.Empty<string>();

    public List<AgentTask> Tasks { get; init; } = new();
    [JsonInclude] public PlanStatus Status { get; private set; } = PlanStatus.Draft;
    [JsonInclude] public int Revision { get; private set; } = 1;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonInclude] public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public bool HasOpenQuestions => OpenQuestions.Any(q => !q.IsAnswered);

    public void SetQuestions(IReadOnlyList<Clarification> questions)
    {
        if (Status is not (PlanStatus.Draft or PlanStatus.InReview))
        {
            throw new InvalidOperationException($"Questions can only change while Draft/InReview (plan is {Status}).");
        }

        OpenQuestions = questions;
        Touch();
    }

    public void MarkInReview()
    {
        if (Status != PlanStatus.Draft)
        {
            throw new InvalidOperationException($"Only a Draft plan can go InReview (plan is {Status}).");
        }

        Status = PlanStatus.InReview;
        Touch();
    }

    public void MarkApproved()
    {
        if (Status != PlanStatus.InReview)
        {
            throw new InvalidOperationException($"Only an InReview plan can be approved (plan is {Status}).");
        }

        if (HasOpenQuestions)
        {
            throw new InvalidOperationException("Plan has unanswered clarification questions.");
        }

        if (Tasks.Count == 0)
        {
            throw new InvalidOperationException("An empty plan cannot be approved.");
        }

        Status = PlanStatus.Approved;
        Touch();
    }

    public void MarkExecuting()
    {
        if (Status != PlanStatus.Approved)
        {
            throw new InvalidOperationException($"Only an Approved plan can execute (plan is {Status}).");
        }

        Status = PlanStatus.Executing;
        Touch();
    }

    public void MarkAuditing() => TransitionTo(PlanStatus.Auditing, PlanStatus.Executing);
    public void MarkCompleted() => TransitionTo(PlanStatus.Completed, PlanStatus.Auditing);
    public void MarkCancelled() => TransitionTo(PlanStatus.Cancelled);
    public void MarkFailed() => TransitionTo(PlanStatus.Failed);

    /// <summary>
    /// La auditoría final (§9) puede ampliar el plan: solo permitido en <c>Auditing</c>,
    /// genera una nueva revisión y vuelve a <c>Executing</c>.
    /// </summary>
    public void ExtendAfterAudit(IEnumerable<AgentTask> additionalTasks)
    {
        if (Status != PlanStatus.Auditing)
        {
            throw new InvalidOperationException("Plans can only grow during the final audit.");
        }

        var extra = additionalTasks.ToList();
        if (extra.Count == 0)
        {
            throw new ArgumentException("No additional tasks provided.", nameof(additionalTasks));
        }

        Tasks.AddRange(extra);
        Revision++;
        Status = PlanStatus.Executing;
        Touch();
    }

    /// <summary>
    /// Reencola tareas a medias (InProgress→Pending) con una indicación del
    /// usuario. Devuelve cuántas. No toca cerradas. Para "seguir" sin finalizar.
    /// </summary>
    public int RequeueInflight(string nudge)
    {
        var count = 0;
        foreach (var task in Tasks.Where(t => t.Status == AgentTaskStatus.InProgress))
        {
            task.Requeue(nudge);
            count++;
        }

        if (count > 0)
        {
            Touch();
        }

        return count;
    }

    public bool AllTasksClosed() =>
        Tasks.Count > 0 && Tasks.All(t =>
            t.Status is AgentTaskStatus.Completed or AgentTaskStatus.Skipped);

    private void TransitionTo(PlanStatus next, PlanStatus? required = null)
    {
        if (required.HasValue && Status != required.Value)
        {
            throw new InvalidOperationException($"Plan cannot go {Status} -> {next} (requires {required}).");
        }

        Status = next;
        Touch();
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

/// <summary>Pregunta de aclaración al usuario (§5). Bloquea la aprobación hasta responderse.</summary>
public sealed class Clarification
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Question { get; init; }
    public string? Context { get; init; }
    public IReadOnlyList<string> SuggestedOptions { get; init; } = Array.Empty<string>();
    [JsonInclude] public string? Answer { get; private set; }
    public bool IsAnswered => Answer is not null;

    public void AnswerQuestion(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new ArgumentException("Answer cannot be empty.", nameof(answer));
        }

        Answer = answer;
    }
}

/// <summary>Riesgo identificado durante la planificación.</summary>
public sealed record PlanRisk(string Description, string Mitigation);
