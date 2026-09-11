using CaAIA.Domain.Enums;

namespace CaAIA.Domain.Events;

/// <summary>Evento inmutable del bus interno (§25): UI, logging, historial y debugging.</summary>
public sealed record AgentEvent(
    Guid Id,
    AgentEventType Type,
    DateTimeOffset OccurredAt,
    Correlation.CorrelationContext? Correlation,
    string? Summary = null,
    string? PayloadJson = null)
{
    public static AgentEvent Create(
        AgentEventType type,
        Correlation.CorrelationContext? correlation,
        string? summary = null,
        string? payloadJson = null) =>
        new(Guid.NewGuid(), type, DateTimeOffset.UtcNow, correlation, summary, payloadJson);
}

/// <summary>Bus de eventos en memoria con suscripción tipada. Implementación en Infrastructure.</summary>
public interface IEventBus
{
    void Publish(AgentEvent @event);
    IDisposable Subscribe(AgentEventType type, Func<AgentEvent, CancellationToken, Task> handler);
    IDisposable SubscribeAll(Func<AgentEvent, CancellationToken, Task> handler);
}
