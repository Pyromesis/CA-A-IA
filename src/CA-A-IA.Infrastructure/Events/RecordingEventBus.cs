// CA-A-IA — Bus que además persiste: canal en caliente (inner) + historial (store).
// La escritura es fire-and-forget aislada (nunca rompe al publicador); FlushAsync la drena
// para tests y apagado graceful.

using CaAIA.Domain.Enums;
using CaAIA.Domain.Events;
using CaAIA.Domain.Persistence;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure.Events;

/// <summary>
/// Decora <see cref="IEventBus"/> con persistencia. Solo se almacenan eventos con
/// correlación de sesión (los globales sin sesión siguen fluyendo en caliente).
/// </summary>
public sealed class RecordingEventBus : IEventBus, IAsyncDisposable
{
    private readonly IEventBus _inner;
    private readonly IEventStore _store;
    private readonly ILogger<RecordingEventBus> _log;
    private int _pending;
    private bool _disposed;

    public RecordingEventBus(IEventBus inner, IEventStore store, ILogger<RecordingEventBus> log)
    {
        _inner = inner;
        _store = store;
        _log = log;
    }

    public void Publish(AgentEvent @event)
    {
        if (_disposed)
        {
            return;
        }

        _inner.Publish(@event);
        if (@event.Correlation is null)
        {
            return;
        }

        Interlocked.Increment(ref _pending);
        _ = WriteAsync(@event);
    }

    public IDisposable Subscribe(AgentEventType type, Func<AgentEvent, CancellationToken, Task> handler) =>
        _inner.Subscribe(type, handler);

    public IDisposable SubscribeAll(Func<AgentEvent, CancellationToken, Task> handler) =>
        _inner.SubscribeAll(handler);

    /// <summary>Espera (acotado) a que los writes pendientes terminen.</summary>
    public async Task FlushAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref _pending) > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(AgentEvent @event)
    {
        try
        {
            await _store.AppendAsync(@event, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Event persistence failed for {Type}", @event.Type);
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await FlushAsync().ConfigureAwait(false);
        if (_inner is IAsyncDisposable asyncInner)
        {
            await asyncInner.DisposeAsync().ConfigureAwait(false);
        }
        else if (_inner is IDisposable syncInner)
        {
            syncInner.Dispose();
        }
    }
}
