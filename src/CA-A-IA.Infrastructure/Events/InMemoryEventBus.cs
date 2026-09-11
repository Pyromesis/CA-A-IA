// CA-A-IA — Bus de eventos en memoria (§25). La persistencia la añade RecordingEventBus.

using System.Collections.Concurrent;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Events;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure.Events;

/// <summary>
/// Implementación en memoria, thread-safe. Los handlers se invocan en orden de suscripción;
/// un handler que falla no bloquea a los demás (se registra y se continúa).
/// </summary>
public sealed class InMemoryEventBus : IEventBus, IDisposable
{
    private readonly ConcurrentDictionary<Guid, (AgentEventType? Type, Func<AgentEvent, CancellationToken, Task> Handler)> _subs = new();
    private readonly ILogger<InMemoryEventBus> _log;
    private bool _disposed;

    public InMemoryEventBus(ILogger<InMemoryEventBus> log)
    {
        _log = log;
    }

    public void Publish(AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (_disposed)
        {
            return;
        }

        foreach (var (_, (type, handler)) in _subs.ToArray())
        {
            if (type.HasValue && type.Value != @event.Type)
            {
                continue;
            }

            try
            {
                // Fire-and-forget con continuación observada: nunca lanzar en el publicador.
                _ = handler(@event, CancellationToken.None).ContinueWith(
                    t =>
                    {
                        if (t.IsFaulted && t.Exception is not null)
                        {
                            _log.LogError(t.Exception, "Event handler failed for {EventType}", @event.Type);
                        }
                    },
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Event handler threw synchronously for {EventType}", @event.Type);
            }
        }
    }

    public IDisposable Subscribe(AgentEventType type, Func<AgentEvent, CancellationToken, Task> handler) =>
        Add(type, handler);

    public IDisposable SubscribeAll(Func<AgentEvent, CancellationToken, Task> handler) =>
        Add(null, handler);

    private IDisposable Add(AgentEventType? type, Func<AgentEvent, CancellationToken, Task> handler)
    {
        var id = Guid.NewGuid();
        _subs[id] = (type, handler ?? throw new ArgumentNullException(nameof(handler)));
        return new Subscription(id, _subs);
    }

    public void Dispose()
    {
        _disposed = true;
        _subs.Clear();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Guid _id;
        private readonly ConcurrentDictionary<Guid, (AgentEventType?, Func<AgentEvent, CancellationToken, Task>)> _subs;

        public Subscription(Guid id, ConcurrentDictionary<Guid, (AgentEventType?, Func<AgentEvent, CancellationToken, Task>)> subs)
        {
            _id = id;
            _subs = subs;
        }

        public void Dispose() => _subs.TryRemove(_id, out _);
    }
}
