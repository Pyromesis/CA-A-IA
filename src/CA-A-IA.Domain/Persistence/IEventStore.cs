// CA-A-IA — Almacén de eventos (persistencia + replay para historial y auditoría).

using CaAIA.Domain.Events;

namespace CaAIA.Domain.Persistence;

/// <summary>
/// Eventos persistidos. El bus en memoria sigue siendo el canal en caliente; este store es
/// el historial consultable (UI, debugging, auditoría). Implementación en Infrastructure.
/// </summary>
public interface IEventStore
{
    Task AppendAsync(AgentEvent @event, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentEvent>> ReadAsync(Guid? sessionId, int maxEntries, CancellationToken cancellationToken);
}
