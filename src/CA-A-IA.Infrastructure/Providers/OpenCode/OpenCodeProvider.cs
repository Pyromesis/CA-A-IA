// CA-A-IA — Adaptador OpenCode (integración principal): `opencode serve` HTTP + lifecycle.
// Mecanismo verificado en opencode.ai/docs (CLI `run/models/serve`, API server, tipos del SDK).
// OpenCode ejecuta SUS propias herramientas en SU workspace: Capabilities.Tools = false
// (honesto: no puede ejecutar nuestras ToolDefinition). Sin binario → indisponible limpio.

using CaAIA.Domain.AI;
using CaAIA.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Infrastructure.Providers.OpenCode;

/// <summary>Opciones del adaptador OpenCode (sección <c>CaAIA:Providers:OpenCode</c>).</summary>
public sealed class OpenCodeOptions
{
    public string Command { get; init; } = "opencode";
    public string Hostname { get; init; } = "127.0.0.1";
    /// <summary>Puerto del servidor; 0 = libre automático.</summary>
    public int Port { get; init; }
    /// <summary>Workspace donde OpenCode ejecuta sus herramientas. Vacío = actual.</summary>
    public string Directory { get; init; } = string.Empty;
    public int StartupTimeoutSeconds { get; init; } = 60;
    public int DefaultTimeoutSeconds { get; init; } = 300;
    /// <summary>Usuario de Basic auth (convención OpenCode: `opencode` por defecto).</summary>
    public string Username { get; init; } = "opencode";
    public string PasswordSecretName { get; init; } = "opencode-server-password";
}

public sealed class OpenCodeProvider : IAIProvider, IAsyncDisposable
{
    public const string ProviderId = "opencode";

    private readonly OpenCodeOptions _options;
    private readonly ISecretStore _secrets;
    private readonly Process.ProcessRunner _runner;
    private readonly Domain.Persistence.IAgentSessionStore? _sessions;
    private readonly ILogger _clientLog;
    private readonly ILogger _lifecycleLog;
    private readonly Domain.Events.IEventBus? _events;
    private readonly HttpClient? _injectedHttp;
    private readonly IOpenCodeServerLifecycle? _injectedLifecycle;

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private HttpClient? _http;
    private OpenCodeServerClient? _client;
    private IOpenCodeServerLifecycle? _lifecycle;
    private bool _disposed;

    public string Id => ProviderId;
    public string DisplayName => "OpenCode";

    /// <summary>
    /// OpenCode delega TODO (incluidas herramientas) al servidor: no acepta nuestras
    /// ToolDefinition ni streaming SSE propio (StreamAsync emite la respuesta completa).
    /// </summary>
    public ProviderCapabilities Capabilities => ProviderCapabilities.None;

    public OpenCodeProvider(
        IOptions<OpenCodeOptions> options,
        ISecretStore secrets,
        Process.ProcessRunner runner,
        ILogger<OpenCodeServerClient> clientLog,
        ILogger<ProcessServerLifecycle> lifecycleLog,
        Domain.Events.IEventBus? events = null,
        Domain.Persistence.IAgentSessionStore? sessions = null)
        : this(options, secrets, runner, clientLog, lifecycleLog, null, null, events, sessions)
    {
    }

    internal OpenCodeProvider(
        IOptions<OpenCodeOptions> options,
        ISecretStore secrets,
        Process.ProcessRunner runner,
        ILogger clientLog,
        ILogger lifecycleLog,
        HttpClient? http,
        IOpenCodeServerLifecycle? lifecycle,
        Domain.Events.IEventBus? events = null,
        Domain.Persistence.IAgentSessionStore? sessions = null)
    {
        _options = options.Value;
        _secrets = secrets;
        _runner = runner;
        _clientLog = clientLog;
        _lifecycleLog = lifecycleLog;
        _events = events;
        _sessions = sessions;
        _injectedHttp = http;
        _injectedLifecycle = lifecycle;
    }

    public async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var timeout = request.Timeout ?? TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds);
        var (providerId, modelId) = SplitModel(request.ModelId);
        var directory = await ResolveDirectoryAsync(request, ct).ConfigureAwait(false);

        OpenCodeSession session;
        try
        {
            session = await client.CreateSessionAsync(TitleOf(request), directory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException)
        {
            throw Map(ex);
        }

        var prompt = string.Join("\n\n", request.Messages
            .Where(m => m.Role is AIRole.System or AIRole.User)
            .Select(m => m.Role == AIRole.System ? $"[system]\n{m.Content}" : m.Content));
        if (request.Tools.Count > 0)
        {
            _clientLog.LogDebug("OpenCode ignores {Tools} caller tool definitions (server-side tools).",
                request.Tools.Count);
        }

        OpenCodeAnswer answer;
        _events?.Publish(Domain.Events.AgentEvent.Create(
            Domain.Enums.AgentEventType.ToolStarted, request.Correlation, "OpenCode started."));
        // Las herramientas corren en el SERVIDOR: se observan sondeando sus
        // mensajes (best-effort) para narrar "leyendo/editando/ejecutando" en vivo.
        // El set compartido evita burbujas duplicadas entre sondeo y cierre.
        var seenTools = new HashSet<string>(StringComparer.Ordinal);
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var poller = PollServerToolsAsync(client, session.Id, request.Correlation, seenTools, pollCts.Token);
        try
        {
            answer = await client.PromptAsync(session.Id, prompt, providerId, modelId, directory, timeout, ct,
                NormalizeEffort(request.ReasoningEffort)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelación/timeout: desatascar también el LADO SERVIDOR, que si no
            // sigue quemando tokens en segundo plano.
            _ = client.AbortSessionAsync(session.Id, CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException)
        {
            _events?.Publish(Domain.Events.AgentEvent.Create(
                Domain.Enums.AgentEventType.ToolCompleted, request.Correlation, $"OpenCode: {ex.Message}"));
            throw Map(ex);
        }
        finally
        {
            pollCts.Cancel();
            try
            {
                await poller.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // El sondeo nunca rompe la respuesta.
            }
        }

        // Cierre: lo visto en el último sondeo + lo que traiga la respuesta
        // (puede incluir herramientas de mensajes previos de la sesión).
        // Solo Completed: ya ocurrieron; los Started en vivo los puso el sondeo.
        var closing = new Dictionary<string, OpenCodeServerToolCall>(StringComparer.Ordinal);
        try
        {
            foreach (var call in await client.GetSessionToolCallsAsync(
                session.Id, CancellationToken.None).ConfigureAwait(false))
            {
                closing[call.Key] = call;
            }
        }
        catch (Exception ex)
        {
            _clientLog.LogDebug(ex, "OpenCode closing tool fetch failed (non-fatal).");
        }

        foreach (var call in answer.ToolCalls ?? Enumerable.Empty<OpenCodeServerToolCall>())
        {
            closing[call.Key] = call;
        }

        foreach (var call in closing.Values)
        {
            PublishServerToolCompleted(call, request.Correlation, seenTools);
        }

        _events?.Publish(Domain.Events.AgentEvent.Create(
            Domain.Enums.AgentEventType.ToolCompleted, request.Correlation, "OpenCode: ok"));

        return new AIResponse(answer.Text, Array.Empty<AIToolCall>(), request.ModelId,
            new TokenUsage(answer.InputTokens, answer.OutputTokens), FinishReason: "stop");
    }

    /// <summary>
    /// Sondea los mensajes de la sesión (cada 2,5 s) y traduce herramientas del
    /// servidor a eventos ToolStarted/ToolCompleted: el Chat ya sabe narrarlos
    /// ("Leyendo X…", "✎ Editó Y", "▶ Ejecutó Z"). Todo best-effort.
    /// </summary>
    private async Task PollServerToolsAsync(
        OpenCodeServerClient client,
        string sessionId,
        Domain.Correlation.CorrelationContext? correlation,
        HashSet<string> seen,
        CancellationToken ct)
    {
        var pending = new Dictionary<string, OpenCodeServerToolCall>(StringComparer.Ordinal);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2.5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                IReadOnlyList<OpenCodeServerToolCall> calls;
                try
                {
                    calls = await client.GetSessionToolCallsAsync(sessionId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _clientLog.LogDebug(ex, "OpenCode tool poll failed (non-fatal).");
                    continue;
                }

                foreach (var call in calls)
                {
                    ct.ThrowIfCancellationRequested();
                    if (seen.Contains(call.Key))
                    {
                        // ¿Terminó desde el último sondeo?
                        if (pending.TryGetValue(call.Key, out _) && IsDone(call.State))
                        {
                            pending.Remove(call.Key);
                            PublishServerToolCompleted(call, correlation, seen);
                        }

                        continue;
                    }

                    seen.Add(call.Key);
                    PublishServerToolStarted(call, correlation);
                    if (IsDone(call.State))
                    {
                        PublishServerToolCompleted(call, correlation, seen);
                    }
                    else
                    {
                        pending[call.Key] = call;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsDone(string state) =>
        state is "completed" or "done" or "success" or "error" or "failed";

    private static bool IsError(string state) => state is "error" or "failed";

    /// <summary>Traduce la herramienta del servidor a nuestros ids (reusa la narración del Chat).</summary>
    internal static string MapServerTool(string tool) =>
        tool.Trim().ToLowerInvariant() switch
        {
            "read" => "ReadFile",
            "list" or "glob" => "ListDirectory",
            "grep" or "search" => "SearchText",
            "edit" or "patch" or "apply" => "EditFile",
            "write" or "create" => "WriteFile",
            "bash" or "shell" or "exec" or "run" or "command" => "ExecuteCommand",
            _ => "OpenCode",
        };

    private void PublishServerToolStarted(
        OpenCodeServerToolCall call, Domain.Correlation.CorrelationContext? correlation)
    {
        if (_events is null)
        {
            return;
        }

        var id = MapServerTool(call.Tool);
        if (id == "OpenCode")
        {
            return; // ruido: el "Trabajando en OpenCode…" base ya está activo
        }

        // Sin título extraíble, el nombre de la herramienta ("Ejecutando bash…")
        // antes que el genérico vacío ("Ejecutando comando…").
        var title = string.IsNullOrWhiteSpace(call.Title) ? call.Tool : call.Title;
        _events.Publish(Domain.Events.AgentEvent.Create(
            Domain.Enums.AgentEventType.ToolStarted, correlation,
            $"{id} started.", ServerArgsJson(id, title)));
    }

    private void PublishServerToolCompleted(
        OpenCodeServerToolCall call,
        Domain.Correlation.CorrelationContext? correlation,
        HashSet<string> seen)
    {
        if (_events is null || !seen.Add(call.Key + ":done"))
        {
            return;
        }

        var id = MapServerTool(call.Tool);
        if (id == "OpenCode")
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(call.Title) ? call.Tool : call.Title;
        var ok = !IsError(call.State);
        _events.Publish(Domain.Events.AgentEvent.Create(
            Domain.Enums.AgentEventType.ToolCompleted, correlation,
            ok ? $"{id}: ok" : $"{id}: {title}",
            ServerArgsJson(id, title)));
    }

    private static string ServerArgsJson(string mappedId, string title)
    {
        var safe = title ?? string.Empty;
        return mappedId == "ExecuteCommand"
            ? System.Text.Json.JsonSerializer.Serialize(new
            {
                command = FirstWord(safe),
                args = new[] { RestWords(safe) },
            })
            : System.Text.Json.JsonSerializer.Serialize(new { path = safe });
    }

    private static string FirstWord(string text)
    {
        var parts = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : string.Empty;
    }

    private static string RestWords(string text)
    {
        var parts = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? string.Join(" ", parts[1..]) : string.Empty;
    }

    public async IAsyncEnumerable<AIStreamChunk> StreamAsync(
        AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // El servidor no expone stream de prompt simple: se emite la respuesta completa como
        // único delta (cancelable antes de empezar). Documentado, no fingido.
        var response = await CompleteAsync(request, ct).ConfigureAwait(false);
        if (response.Content.Length > 0)
        {
            yield return new AIStreamChunk(response.Content);
        }

        yield return new AIStreamChunk(string.Empty, IsFinal: true);
    }

    public async Task<IReadOnlyCollection<AIModel>> GetModelsAsync(CancellationToken ct)
    {
        OpenCodeServerClient client;
        try
        {
            client = await EnsureClientAsync(ct).ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            // Sin binario no hay catálogo local: mensaje accionable (la UI lo muestra).
            // Alternativa sin instalación: el proveedor "opencode-zen".
            throw new NotSupportedException(
                ex.Message + " Models unavailable; use the 'opencode-zen' provider instead.", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            _clientLog.LogDebug(ex, "OpenCode models unavailable.");
            return Array.Empty<AIModel>();
        }

        try
        {
            var models = await client.GetProviderModelsAsync(ct).ConfigureAwait(false);
            return models.Select(m =>
            {
                var caps = ProviderCapabilities.Streaming;
                if (m.SupportsTools)
                {
                    caps |= ProviderCapabilities.Tools;
                }

                if (m.ContextWindow is >= 100_000)
                {
                    caps |= ProviderCapabilities.LongContext;
                }

                if (m.SupportsVision)
                {
                    caps |= ProviderCapabilities.Vision;
                }

                return new AIModel(m.Id, m.Id, ProviderId, m.ContextWindow, caps, IsFree: m.IsFree);
            }).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            _clientLog.LogDebug(ex, "OpenCode models failed.");
            return Array.Empty<AIModel>();
        }
    }

    public async Task<bool> CheckHealthAsync(CancellationToken ct)
    {
        // Ruta completa: Process.Start no resuelve .cmd por PATHEXT (npm instala opencode.cmd).
        var binary = ProcessServerLifecycle.FindBinary(_options.Command);
        if (binary is null)
        {
            return false;
        }

        try
        {
            var (file, args) = Process.ScriptLaunch.Wrap(binary, new[] { "--version" });
            var result = await _runner.RunAsync(file, args,
                Environment.CurrentDirectory, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            return result.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- Internals ----

    /// <summary>
    /// Workspace de la sesión (para que OpenCode actúe en la carpeta del usuario),
    /// con fallback a opciones/directorio actual.
    /// </summary>
    internal async Task<string> ResolveDirectoryAsync(AIRequest request, CancellationToken ct)
    {
        if (_sessions is not null)
        {
            try
            {
                var session = await _sessions.LoadAsync(request.Correlation.SessionId, ct)
                    .ConfigureAwait(false);
                if (session is not null && Directory.Exists(session.WorkspacePath))
                {
                    return session.WorkspacePath;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        return string.IsNullOrWhiteSpace(_options.Directory)
            ? Environment.CurrentDirectory : _options.Directory;
    }

    internal static (string? ProviderId, string? ModelId) SplitModel(string model)
    {
        var slash = model.IndexOf('/');
        if (slash <= 0 || slash == model.Length - 1)
        {
            return (null, null); // sin forma provider/model → default del servidor
        }

        return (model[..slash], model[(slash + 1)..]);
    }

    internal static string TitleOf(AIRequest request)
    {
        var last = request.Messages.LastOrDefault(m => m.Role == AIRole.User)?.Content ?? "CA-A-IA session";
        last = last.Trim().Replace('\n', ' ').Replace('\r', ' ');
        return last.Length > 80 ? last[..80] + "…" : last;
    }

    /// <summary>
    /// Normaliza el esfuerzo a los valores del servidor (minúsculas); vacío/Default → null (omitir).
    /// </summary>
    internal static string? NormalizeEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        var normalized = effort.Trim().ToLowerInvariant();
        return normalized switch
        {
            "minimal" or "low" or "medium" or "high" or "xhigh" => normalized,
            "default" or "none" or "auto" or "" => null,
            _ => null,
        };
    }

    private AIProviderException Map(Exception ex) => ex switch
    {
        TimeoutException => new AIProviderException(Id, AIErrorKind.Timeout, ex.Message, isRetryable: true),
        HttpRequestException => new AIProviderException(Id, AIErrorKind.Network, ex.Message,
            isRetryable: true, inner: ex),
        _ => new AIProviderException(Id, AIErrorKind.ProviderInternal, ex.Message, isRetryable: false, inner: ex),
    };

    private async Task<OpenCodeServerClient> EnsureClientAsync(CancellationToken ct)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            _lifecycle = _injectedLifecycle ?? new ProcessServerLifecycle(
                _options.Command, _options.Hostname, _options.Port,
                TimeSpan.FromSeconds(Math.Max(10, _options.StartupTimeoutSeconds)),
                _lifecycleLog);
            var url = await _lifecycle.StartAsync(ct).ConfigureAwait(false);
            _http = _injectedHttp ?? new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
            _client = new OpenCodeServerClient(_http, url,
                string.IsNullOrWhiteSpace(_options.Username) ? null : _options.Username,
                async innerCt => await _secrets.RetrieveAsync(_options.PasswordSecretName, innerCt)
                    .ConfigureAwait(false)
                    ?? Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD"),
                _clientLog);
            return _client;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_lifecycle is not null)
        {
            await _lifecycle.DisposeAsync().ConfigureAwait(false);
        }

        if (_injectedHttp is null)
        {
            _http?.Dispose();
        }

        _startGate.Dispose();
    }
}
