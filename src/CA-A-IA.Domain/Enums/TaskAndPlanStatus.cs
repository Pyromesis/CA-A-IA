namespace CaAIA.Domain.Enums;

/// <summary>Estado de una tarea individual dentro de un <see cref="Planning.Plan"/>.</summary>
public enum AgentTaskStatus
{
    Unknown = 0,
    Pending = 1,
    InProgress = 2,
    Completed = 3,
    Failed = 4,
    Blocked = 5,
    Skipped = 6,
    NeedsReview = 7,
}

/// <summary>Estado del plan en su conjunto.</summary>
public enum PlanStatus
{
    Unknown = 0,
    Draft = 1,
    InReview = 2,
    Approved = 3,
    Executing = 4,
    Auditing = 5,
    Completed = 6,
    Cancelled = 7,
    Failed = 8,
}
