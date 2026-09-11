namespace CaAIA.Domain.Enums;

/// <summary>Alcance de una entrada de memoria del agente (§17).</summary>
public enum MemoryScope
{
    Unknown = 0,
    Session = 1,
    Task = 2,
    Project = 3,
    Decision = 4,
    Error = 5,
    Verification = 6,
}

/// <summary>Tipos de eventos del bus interno (§25). El payload viaja en <c>CaAIA.Domain.Events.AgentEvent</c>.</summary>
public enum AgentEventType
{
    Unknown = 0,
    AgentStarted = 1,
    AgentPaused = 2,
    AgentResumed = 3,
    AgentStopped = 4,
    PlanCreated = 5,
    TaskStarted = 6,
    TaskCompleted = 7,
    TaskFailed = 8,
    ToolStarted = 9,
    ToolCompleted = 10,
    BuildStarted = 11,
    BuildCompleted = 12,
    TestStarted = 13,
    TestCompleted = 14,
    RepairStarted = 15,
    RepairCompleted = 16,
    CheckpointCreated = 17,
    ProviderConnected = 18,
    ProviderError = 19,
    SessionRecovered = 20,
    StateChanged = 21,
}
