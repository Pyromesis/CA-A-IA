// CA-A-IA — Preferencias de usuario (workspace, proveedor, modelo): en memoria + persistidas.
// Sesión actual de trabajo (runtime, no persistida). Implementaciones sin dependencias externas.

using CaAIA.Application.Configuration;
using CaAIA.Domain.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Application.Services;

/// <summary>
/// Preferencias con valores por defecto de configuración y persistencia en ISettingsStore.
/// </summary>
public interface IUserPreferences
{
    string WorkspacePath { get; set; }
    string ProviderId { get; }
    string ModelId { get; }
    void SetModel(string providerId, string modelId);

    /// <summary>Esfuerzo de razonamiento (wire value minúsculas; "" = Default).</summary>
    string ReasoningEffort { get; }
    void SetReasoningEffort(string effort);

    /// <summary>Nivel de autorización del agente (1 = confirma todo; ver AuthorizationLevel).</summary>
    Domain.Security.AuthorizationLevel AuthorizationLevel { get; }
    void SetAuthorizationLevel(Domain.Security.AuthorizationLevel level);

    /// <summary>Filtro "solo gratis" del catálogo de modelos.</summary>
    bool ShowFreeOnly { get; }
    void SetShowFreeOnly(bool showFreeOnly);

    /// <summary>Equipo de Autonomía: modelo actor (ejecuta) y analista (verifica con visión).</summary>
    string AutonomyActorProvider { get; }
    string AutonomyActorModel { get; }
    string AutonomyAnalystProvider { get; }
    string AutonomyAnalystModel { get; }
    void SetAutonomyActor(string providerId, string modelId);
    void SetAutonomyAnalyst(string providerId, string modelId);
    event EventHandler? Changed;

    /// <summary>Carga lo persistido (una vez, al arrancar). Sin llamar, valen los defaults.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Espera a que los guardados en vuelo terminen (tope interno): llamarlo al
    /// cerrar para no perder el último cambio (proveedor, carpeta, nivel…).
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken);
}

/// <summary>Sesión activa para los paneles (la fija el Chat al crearla).</summary>
public interface ISessionContext
{
    Guid? CurrentSessionId { get; set; }
    event EventHandler? Changed;
}

public sealed class SessionContext : ISessionContext
{
    private Guid? _current;
    public event EventHandler? Changed;

    public Guid? CurrentSessionId
    {
        get => _current;
        set
        {
            if (_current != value)
            {
                _current = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

public sealed class UserPreferences : IUserPreferences
{
    public const string WorkspaceKey = "workspace.path";
    public const string ProviderKey = "model.provider";
    public const string ModelKey = "model.id";
    public const string EffortKey = "reasoning.effort";
    public const string AuthLevelKey = "auth.level";
    public const string FreeOnlyKey = "ui.showFreeOnly";
    public const string ActorProviderKey = "autonomy.actor.provider";
    public const string ActorModelKey = "autonomy.actor.model";
    public const string AnalystProviderKey = "autonomy.analyst.provider";
    public const string AnalystModelKey = "autonomy.analyst.model";

    private readonly object _gate = new();
    private readonly object _pendingGate = new();
    private readonly HashSet<Task> _pendingPersists = new();
    private readonly ISettingsStore _store;
    private readonly ILogger<UserPreferences> _log;    private string _workspace;
    private string _providerId;
    private string _modelId;
    private string _effort = string.Empty;
    private string _actorProvider = string.Empty;
    private string _actorModel = string.Empty;
    private string _analystProvider = string.Empty;
    private string _analystModel = string.Empty;
    private Domain.Security.AuthorizationLevel _authLevel = Domain.Security.AuthorizationLevel.ConfirmChanges;
    private bool _showFreeOnly;
    private bool _initialized;

    public event EventHandler? Changed;

    public UserPreferences(IOptions<CaAIAOptions> options, ISettingsStore store, ILogger<UserPreferences> log)
    {
        var configured = options.Value;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _workspace = Directory.Exists(documents) ? documents : Environment.CurrentDirectory;
        _providerId = configured.Providers.DefaultProviderId;
        _modelId = configured.Providers.DefaultModelId;
        _store = store;
        _log = log;
    }

    public string WorkspacePath
    {
        get { lock (_gate) { return _workspace; } }
        set
        {
            var path = string.IsNullOrWhiteSpace(value) ? _workspace : value.Trim();
            lock (_gate)
            {
                if (_workspace == path)
                {
                    return;
                }

                _workspace = path;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            _ = PersistAsync(WorkspaceKey, path);
        }
    }

    public string ProviderId { get { lock (_gate) { return _providerId; } } }
    public string ModelId { get { lock (_gate) { return _modelId; } } }
    public string ReasoningEffort { get { lock (_gate) { return _effort; } } }
    public Domain.Security.AuthorizationLevel AuthorizationLevel
    {
        get { lock (_gate) { return _authLevel; } }
    }

    public bool ShowFreeOnly { get { lock (_gate) { return _showFreeOnly; } } }

    public string AutonomyActorProvider { get { lock (_gate) { return _actorProvider; } } }
    public string AutonomyActorModel { get { lock (_gate) { return _actorModel; } } }
    public string AutonomyAnalystProvider { get { lock (_gate) { return _analystProvider; } } }
    public string AutonomyAnalystModel { get { lock (_gate) { return _analystModel; } } }

    public void SetAutonomyActor(string providerId, string modelId) =>
        SetPair(ActorProviderKey, ActorModelKey, providerId, modelId,
            (p, m) => { _actorProvider = p; _actorModel = m; },
            () => (_actorProvider, _actorModel));

    public void SetAutonomyAnalyst(string providerId, string modelId) =>
        SetPair(AnalystProviderKey, AnalystModelKey, providerId, modelId,
            (p, m) => { _analystProvider = p; _analystModel = m; },
            () => (_analystProvider, _analystModel));

    private void SetPair(string providerKey, string modelKey, string providerId, string modelId,
        Action<string, string> assign, Func<(string Provider, string Model)> current)
    {
        providerId ??= string.Empty;
        modelId ??= string.Empty;
        lock (_gate)
        {
            var (p, m) = current();
            if (p == providerId && m == modelId)
            {
                return;
            }

            assign(providerId, modelId);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _ = PersistAsync(providerKey, providerId);
        _ = PersistAsync(modelKey, modelId);
    }

    public void SetShowFreeOnly(bool showFreeOnly)
    {
        lock (_gate)
        {
            if (_showFreeOnly == showFreeOnly)
            {
                return;
            }

            _showFreeOnly = showFreeOnly;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _ = PersistAsync(FreeOnlyKey, showFreeOnly ? "1" : string.Empty);
    }

    public void SetModel(string providerId, string modelId)
    {
        providerId ??= string.Empty;
        modelId ??= string.Empty;
        lock (_gate)
        {
            if (_providerId == providerId && _modelId == modelId)
            {
                return;
            }

            _providerId = providerId;
            _modelId = modelId;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _ = PersistAsync(ProviderKey, _providerId);
        _ = PersistAsync(ModelKey, _modelId);
    }

    public void SetReasoningEffort(string effort)
    {
        var normalized = (effort ?? string.Empty).Trim().ToLowerInvariant();
        lock (_gate)
        {
            if (_effort == normalized)
            {
                return;
            }

            _effort = normalized;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _ = PersistAsync(EffortKey, normalized);
    }

    public void SetAuthorizationLevel(Domain.Security.AuthorizationLevel level)
    {
        if (!Enum.IsDefined(level))
        {
            level = Domain.Security.AuthorizationLevel.ConfirmChanges;
        }

        lock (_gate)
        {
            if (_authLevel == level)
            {
                return;
            }

            _authLevel = level;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _ = PersistAsync(AuthLevelKey, ((int)level).ToString());
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
        }

        try
        {
            var workspace = await _store.GetAsync(WorkspaceKey, cancellationToken).ConfigureAwait(false);
            var provider = await _store.GetAsync(ProviderKey, cancellationToken).ConfigureAwait(false);
            var model = await _store.GetAsync(ModelKey, cancellationToken).ConfigureAwait(false);
            var effort = await _store.GetAsync(EffortKey, cancellationToken).ConfigureAwait(false);
            var authLevel = await _store.GetAsync(AuthLevelKey, cancellationToken).ConfigureAwait(false);
            var freeOnly = await _store.GetAsync(FreeOnlyKey, cancellationToken).ConfigureAwait(false);
            var actorProvider = await _store.GetAsync(ActorProviderKey, cancellationToken).ConfigureAwait(false);
            var actorModel = await _store.GetAsync(ActorModelKey, cancellationToken).ConfigureAwait(false);
            var analystProvider = await _store.GetAsync(AnalystProviderKey, cancellationToken).ConfigureAwait(false);
            var analystModel = await _store.GetAsync(AnalystModelKey, cancellationToken).ConfigureAwait(false);
            var changed = false;
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace))
                {
                    _workspace = workspace;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(provider))
                {
                    _providerId = provider;
                    changed = true;
                }

                if (model is not null)
                {
                    _modelId = model;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(effort))
                {
                    _effort = effort.Trim().ToLowerInvariant();
                    changed = true;
                }

                if (int.TryParse(authLevel, out var level)
                    && Enum.IsDefined(typeof(Domain.Security.AuthorizationLevel), level))
                {
                    _authLevel = (Domain.Security.AuthorizationLevel)level;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(freeOnly))
                {
                    _showFreeOnly = freeOnly.Trim() == "1";
                    changed = true;
                }

                if (actorProvider is not null)
                {
                    _actorProvider = actorProvider;
                    changed = true;
                }

                if (actorModel is not null)
                {
                    _actorModel = actorModel;
                    changed = true;
                }

                if (analystProvider is not null)
                {
                    _analystProvider = analystProvider;
                    changed = true;
                }

                if (analystModel is not null)
                {
                    _analystModel = analystModel;
                    changed = true;
                }
            }

            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "User preferences not restored; using defaults.");
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        Task[] snapshot;
        lock (_pendingGate)
        {
            snapshot = _pendingPersists.ToArray();
        }

        if (snapshot.Length == 0)
        {
            return Task.CompletedTask;
        }

        return WaitAllAsync(snapshot, cancellationToken);
    }

    private static async Task WaitAllAsync(Task[] tasks, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await Task.WhenAll(tasks).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort de apagado: lo que no se guardó ya hizo log en PersistCoreAsync.
        }
    }

    private async Task PersistAsync(string key, string value)
    {
        var pending = PersistCoreAsync(key, value);
        lock (_pendingGate)
        {
            _pendingPersists.Add(pending);
        }

        try
        {
            await pending.ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingGate)
            {
                _pendingPersists.Remove(pending);
            }
        }
    }

    private async Task PersistCoreAsync(string key, string value)
    {
        try
        {
            await _store.SetAsync(key, value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Preference {Key} not persisted.", key);
        }
    }
}
