// CA-A-IA · Fase 0 — Factoría de motores (un motor por sesión, §7 reanudación).

using System.Collections.Concurrent;
using CaAIA.Application.Configuration;
using CaAIA.Application.Services;
using CaAIA.Domain.Events;
using CaAIA.Domain.Execution;
using CaAIA.Domain.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Agent.Execution;

/// <summary>
/// Crea y cachea <see cref="AgentExecutionEngine"/> por sesión. El motor consume un
/// <see cref="CancellationTokenSource"/> interno que no se puede reutilizar tras pausar;
/// al reanudar se descarta y se crea uno nuevo hidratado desde el último checkpoint.
/// </summary>
public sealed class AgentEngineFactory : IAgentEngineFactory
{
    private readonly ConcurrentDictionary<Guid, AgentExecutionEngine> _engines = new();
    private readonly IAgentSessionStore _sessions;
    private readonly IPlanStore _plans;
    private readonly ICheckpointStore _checkpoints;
    private readonly IEventBus _events;
    private readonly ITaskExecutor _executor;
    private readonly IPlanVerifier _verifier;
    private readonly IRepairPolicy _repair;
    private readonly IOptions<CaAIAOptions> _options;
    private readonly ILogger<AgentExecutionEngine> _engineLog;
    private readonly LessonStore? _lessons;

    public AgentEngineFactory(
        IAgentSessionStore sessions,
        IPlanStore plans,
        ICheckpointStore checkpoints,
        IEventBus events,
        ITaskExecutor executor,
        IPlanVerifier verifier,
        IRepairPolicy repair,
        IOptions<CaAIAOptions> options,
        ILogger<AgentExecutionEngine> engineLog,
        LessonStore? lessons = null)
    {
        _sessions = sessions;
        _plans = plans;
        _checkpoints = checkpoints;
        _events = events;
        _executor = executor;
        _verifier = verifier;
        _repair = repair;
        _options = options;
        _engineLog = engineLog;
        _lessons = lessons;
    }

    public IAgentExecutionEngine GetOrCreate(Guid sessionId) =>
        _engines.GetOrAdd(sessionId, id => new AgentExecutionEngine(
            id, _sessions, _plans, _checkpoints, _events, _executor, _verifier, _repair,
            _options, _engineLog, lessons: _lessons));

    public IAgentExecutionEngine Recreate(Guid sessionId)
    {
        if (_engines.TryRemove(sessionId, out var old))
        {
            _ = old.DisposeAsync().AsTask();
        }

        var fresh = new AgentExecutionEngine(
            sessionId, _sessions, _plans, _checkpoints, _events, _executor, _verifier, _repair,
            _options, _engineLog,
            global::CaAIA.Domain.Enums.AgentState.Paused, lessons: _lessons);
        _engines[sessionId] = fresh;
        return fresh;
    }

    public bool Remove(Guid sessionId)
    {
        if (_engines.TryRemove(sessionId, out var engine))
        {
            _ = engine.DisposeAsync().AsTask();
            return true;
        }

        return false;
    }
}
