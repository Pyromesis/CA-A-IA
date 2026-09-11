namespace CaAIA.Domain.Correlation;

/// <summary>Identificadores de correlación para observabilidad (§26): reconstruyen qué hizo el agente, cuándo y por qué.</summary>
public sealed record CorrelationContext(
    Guid CorrelationId,
    Guid SessionId,
    Guid ExecutionId,
    Guid? TaskId = null,
    string? ProviderRequestId = null)
{
    public static CorrelationContext Create(Guid sessionId, Guid executionId, Guid? taskId = null) =>
        new(Guid.NewGuid(), sessionId, executionId, taskId);

    public CorrelationContext WithTask(Guid taskId) => this with { TaskId = taskId };
    public CorrelationContext WithProviderRequest(string requestId) => this with { ProviderRequestId = requestId };
}
