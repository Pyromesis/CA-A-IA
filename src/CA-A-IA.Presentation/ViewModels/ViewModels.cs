// CA-A-IA · Fase 0 — ViewModels MVVM (CommunityToolkit.Mvvm). Sin lógica de negocio:
// orquestan ISessionCoordinator + observan IEventBus, siempre fuera del UI thread salvo binding.
//
// NOTA (MVVMTK0045): se usan campos con [ObservableProperty] en lugar de partial properties
// porque el generador disponible implementa correctamente esta forma en WinUI 3 desempaquetado.
// Fase 0 no publica con trimming/AOT; migrar a partial properties antes del empaquetado MSIX.
#pragma warning disable MVVMTK0045

using System.Collections.ObjectModel;
using CaAIA.Application.DTOs;
using CaAIA.Application.Services;
using CaAIA.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CaAIA.Presentation.ViewModels;

/// <summary>Barra de estado: estado del agente + sesión activa (se actualiza vía eventos).</summary>
public sealed partial class StatusBarViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable _subscription;
    private readonly Application.Services.IUserPreferences _prefs;

    // Partial properties (no [ObservableProperty] sobre fields): compatibles con AOT/WinRT (MVVMTK0045).
    [ObservableProperty]
    private string _agentState = string.Empty;
    [ObservableProperty]
    private string _sessionInfo = string.Empty;
    [ObservableProperty]
    private string _providerInfo = string.Empty;

    /// <summary>Punto de color del estado (verde reposo, ámbar trabajando, azul pausa, rojo fallo).</summary>
    [ObservableProperty]
    private Microsoft.UI.Xaml.Media.SolidColorBrush _stateBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x3D, 0xDC, 0x84));

    partial void OnAgentStateChanged(string value) => StateBrush = BrushForState(value);

    internal static Microsoft.UI.Xaml.Media.SolidColorBrush BrushForState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return new(Microsoft.UI.Colors.Gray);
        }

        if (state.Contains("Idle", StringComparison.OrdinalIgnoreCase))
        {
            return new(Windows.UI.Color.FromArgb(0xFF, 0x3D, 0xDC, 0x84)); // verde
        }

        if (state.Contains("Paus", StringComparison.OrdinalIgnoreCase))
        {
            return new(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF)); // azul
        }

        if (state.Contains("Cancell", StringComparison.OrdinalIgnoreCase)
            || state.Contains("Fail", StringComparison.OrdinalIgnoreCase)
            || state.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            return new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x72, 0x62)); // rojo
        }

        return new(Windows.UI.Color.FromArgb(0xFF, 0xF0, 0xA9, 0x2E)); // ámbar: trabajando
    }

    public StatusBarViewModel(Domain.Events.IEventBus events, Application.Services.IUserPreferences prefs)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("StatusBarViewModel must be created on the UI thread.");
        _agentState = global::CaAIA.Domain.Enums.AgentState.Idle.ToString();
        _sessionInfo = "No session";
        _prefs = prefs;
        RefreshProviderInfo();
        _prefs.Changed += (_, _) =>
        {
            if (_dispatcher.HasThreadAccess)
            {
                RefreshProviderInfo();
            }
            else
            {
                _dispatcher.TryEnqueue(RefreshProviderInfo);
            }
        };
        _subscription = events.SubscribeAll(OnEventAsync);
    }

    private void RefreshProviderInfo()
    {
        var model = string.IsNullOrWhiteSpace(_prefs.ModelId) ? "auto" : _prefs.ModelId;
        ProviderInfo = $"{_prefs.ProviderId} / {model}";
    }

    private Task OnEventAsync(Domain.Events.AgentEvent e, CancellationToken ct)
    {
        // La barra muestra ESTADOS, no resúmenes (los resúmenes van al Chat/Salida).
        // El handler corre en threadpool: el binding solo se toca en el hilo UI.
        string? state = e.Type switch
        {
            AgentEventType.AgentStarted => global::CaAIA.Domain.Enums.AgentState.Idle.ToString(),
            AgentEventType.AgentPaused => global::CaAIA.Domain.Enums.AgentState.Paused.ToString(),
            AgentEventType.AgentResumed => "Running",
            AgentEventType.AgentStopped => global::CaAIA.Domain.Enums.AgentState.Cancelled.ToString(),
            AgentEventType.StateChanged => ParseState(e.Summary),
            _ => null,
        };
        if (state is not null)
        {
            var captured = state;
            _dispatcher.TryEnqueue(() =>
            {
                AgentState = captured;
                // La sesión también avanza con el motor: si no, la barra muestra
                // "Executing … Idle" a la vez y nadie sabe qué pasa.
                if (_sessionId.HasValue)
                {
                    SessionInfo = $"Session {_sessionId.Value:N} · {captured}";
                }
            });
        }

        return Task.CompletedTask;
    }

    private static string? ParseState(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        // Formato del motor: "From -> To: reason".
        var arrow = summary.IndexOf("->", StringComparison.Ordinal);
        if (arrow < 0)
        {
            return summary;
        }

        var rest = summary[(arrow + 2)..].Trim();
        var colon = rest.IndexOf(':');
        return (colon < 0 ? rest : rest[..colon]).Trim();
    }

    public void SetSession(SessionDto? session)
    {
        _sessionId = session?.Id;
        SessionInfo = session is null ? "No session" : $"Session {session.Id:N} · {session.LastKnownState}";
    }

    private Guid? _sessionId;

    public void Dispose() => _subscription.Dispose();
}
/// <summary>Rol de un mensaje visible en el chat.</summary>
public enum ChatRole
{
    System,
    User,
    Agent,
}

/// <summary>Mensaje del chat (burbuja).</summary>
public sealed record ChatMessage(ChatRole Role, string Text, DateTimeOffset At)
{
    public string RoleLabel => Role switch
    {
        ChatRole.User => "Tú",
        ChatRole.Agent => "CA-A-IA",
        _ => "Sistema",
    };

    public string TimeLabel => At.ToLocalTime().ToString("HH:mm");
}

/// <summary>Opción de modelo del selector (proveedor + modelo; modelo vacío = default del proveedor).</summary>
public sealed record ModelOption(
    string ProviderId,
    string ModelId,
    bool IsFree = false,
    IReadOnlyList<string>? SupportedEfforts = null)
{
    public string Display => (string.IsNullOrWhiteSpace(ModelId) ? "auto (default)" : ModelId)
        + (IsFree ? " · gratis" : string.Empty);

    /// <summary>¿Acepta este nivel de esfuerzo? Vacío en el modelo = se aceptan todos.</summary>
    public bool AcceptsEffort(string wireValue) =>
        SupportedEfforts is null || SupportedEfforts.Count == 0
        || string.IsNullOrEmpty(wireValue)
        || SupportedEfforts.Contains(wireValue, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Opción de proveedor del selector.</summary>
public sealed record ProviderOption(string Id, string DisplayName)
{
    public string Display => string.IsNullOrWhiteSpace(DisplayName) ? Id : $"{DisplayName} ({Id})";
}

/// <summary>Nivel de esfuerzo de razonamiento (como en OpenCode). Vacío = Default.</summary>
public sealed record EffortOption(string Display, string Value);

/// <summary>Nivel de autorización del agente (1 = pide permiso, 2 = edita sin PC, 3 = total).</summary>
public sealed record AuthLevelOption(string Display, int Level);

/// <summary>Chat literal: historial con burbujas, carpeta por selector y modelo elegible.</summary>
public sealed partial class ChatViewModel : ObservableObject, IDisposable
{
    private const int MaxMessages = 200;

    private readonly ISessionCoordinator _coordinator;
    private readonly StatusBarViewModel _status;
    private readonly Domain.AI.IProviderRegistry _providers;
    private readonly Application.Services.IUserPreferences _prefs;
    private readonly Application.Services.ISessionContext _sessions;
    private readonly Domain.Persistence.IChatMessageStore _history;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable _subscription;
    private readonly Diagnostics.UiFlightRecorder _flight; // TEMPORARY-DIAGNOSTIC

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public ObservableCollection<ProviderOption> AvailableProviders { get; } = new();
    public ObservableCollection<ModelOption> AvailableModels { get; } = new();
    public ObservableCollection<EffortOption> AvailableEfforts { get; } = new();
    public ObservableCollection<AuthLevelOption> AvailableAuthLevels { get; } = new()
    {
        new("Nivel 1 — Permiso por cambio", 1),
        new("Nivel 2 — Edita sin pedir, sin PC", 2),
        new("Nivel 3 — Control total sin pedir", 3),
    };

    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private string _agentStatus = string.Empty;

    [ObservableProperty]
    private bool _isAgentWorking;

    /// <summary>Motor en pausa cooperativa (reanudable en la misma sesión).</summary>
    [ObservableProperty]
    private bool _isAgentPaused;

    public Visibility AgentStatusVisibility =>
        IsAgentWorking || !string.IsNullOrWhiteSpace(AgentStatus)
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Botón ⏸: solo mientras trabaja sin pausa.</summary>
    public Visibility PauseButtonVisibility =>
        IsAgentWorking && !IsAgentPaused ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Botón ▶: solo en pausa (continúa la misma sesión).</summary>
    public Visibility ResumeButtonVisibility =>
        IsAgentPaused ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Botón ⏹: mientras trabaja o está en pausa.</summary>
    public Visibility StopButtonVisibility =>
        IsAgentWorking || IsAgentPaused ? Visibility.Visible : Visibility.Collapsed;

    partial void OnAgentStatusChanged(string value) =>
        OnPropertyChanged(nameof(AgentStatusVisibility));

    partial void OnIsAgentWorkingChanged(bool value)
    {
        OnPropertyChanged(nameof(AgentStatusVisibility));
        OnPropertyChanged(nameof(PauseButtonVisibility));
        OnPropertyChanged(nameof(StopButtonVisibility));
    }

    partial void OnIsAgentPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(PauseButtonVisibility));
        OnPropertyChanged(nameof(ResumeButtonVisibility));
        OnPropertyChanged(nameof(StopButtonVisibility));
    }

    private void SetPaused(bool paused)
    {
        if (_dispatcher.HasThreadAccess)
        {
            IsAgentPaused = paused;
        }
        else
        {
            _dispatcher.TryEnqueue(() => { IsAgentPaused = paused; });
        }
    }

    private void SetActivity(string? text, bool working = true)
    {
        if (text is null)
        {
            return;
        }

        // Los eventos llegan en threadpool: todo binding se toca en el hilo UI.
        if (_dispatcher.HasThreadAccess)
        {
            AgentStatus = text;
            IsAgentWorking = working;
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                AgentStatus = text;
                IsAgentWorking = working;
            });
        }
    }

    private void ClearActivity()
    {
        if (_dispatcher.HasThreadAccess)
        {
            AgentStatus = string.Empty;
            IsAgentWorking = false;
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                AgentStatus = string.Empty;
                IsAgentWorking = false;
            });
        }
    }

    [ObservableProperty]
    private string _workspacePath = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoadingModels;

    [ObservableProperty]
    private string _modelsStatus = string.Empty;

    [ObservableProperty]
    private ModelOption? _selectedModel;

    [ObservableProperty]
    private ProviderOption? _selectedProvider;

    [ObservableProperty]
    private bool _showFreeOnly;

    [ObservableProperty]
    private EffortOption _selectedEffort = new("Default", string.Empty);

    partial void OnSelectedEffortChanged(EffortOption? value)
    {
        if (_suppressSelectionEvents || value is null)
        {
            return;
        }

        _prefs.SetReasoningEffort(value.Value);
    }

    [ObservableProperty]
    private AuthLevelOption _selectedAuthLevel = new("Nivel 1 — Permiso por cambio", 1);

    partial void OnSelectedAuthLevelChanged(AuthLevelOption? value)
    {
        if (_suppressSelectionEvents || value is null)
        {
            return;
        }

        if (Enum.IsDefined(typeof(Domain.Security.AuthorizationLevel), value.Level))
        {
            _prefs.SetAuthorizationLevel((Domain.Security.AuthorizationLevel)value.Level);
        }
    }

    private void SyncAuthLevelFromPrefs()
    {
        var current = AvailableAuthLevels.FirstOrDefault(o => o.Level == (int)_prefs.AuthorizationLevel)
            ?? AvailableAuthLevels[0];
        if (!Equals(SelectedAuthLevel, current))
        {
            _suppressSelectionEvents = true;
            try
            {
                SelectedAuthLevel = current;
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }
    }

    partial void OnSelectedProviderChanged(ProviderOption? value)
    {
        if (_suppressSelectionEvents || value is null)
        {
            return;
        }

        _prefs.SetModel(value.Id, string.Empty);
        _ = ReloadModelsForProviderAsync(value.Id, CancellationToken.None);
    }

    partial void OnShowFreeOnlyChanged(bool value)
    {
        if (_suppressSelectionEvents)
        {
            return;
        }

        _prefs.SetShowFreeOnly(value);
        RefreshSuggestions();
    }

    partial void OnSelectedModelChanged(ModelOption? value)
    {
        if (_suppressSelectionEvents || value is null || SelectedProvider is null)
        {
            return;
        }

        _prefs.SetModel(SelectedProvider.Id, value.ModelId);
        RebuildEfforts();
    }

    /// <summary>
    /// El combo de esfuerzo muestra Default + lo que el modelo acepta (si el catálogo lo dice).
    /// Sin información, los 6 niveles (cada proveedor ignora lo que no soporta).
    /// </summary>
    private void RebuildEfforts()
    {
        var current = SelectedEffort;
        AvailableEfforts.Clear();
        foreach (var option in AllEfforts.Where(e =>
            string.IsNullOrEmpty(e.Value) || SelectedModel is null || SelectedModel.AcceptsEffort(e.Value)))
        {
            AvailableEfforts.Add(option);
        }

        var keep = current is not null
            ? AvailableEfforts.FirstOrDefault(e => e.Value == current.Value)
            : null;
        _suppressSelectionEvents = true;
        try
        {
            SelectedEffort = keep ?? AvailableEfforts[0];
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        _prefs.SetReasoningEffort(SelectedEffort.Value);
    }

    private static readonly IReadOnlyList<EffortOption> AllEfforts = new[]
    {
        new EffortOption("Default", string.Empty),
        new EffortOption("Minimal", "minimal"),
        new EffortOption("Low", "low"),
        new EffortOption("Medium", "medium"),
        new EffortOption("High", "high"),
        new EffortOption("Xhigh", "xhigh"),
    };

    private bool _suppressSelectionEvents;
    private bool _viewingHistory;
    private List<ModelOption> _allModels = new();
    private CancellationTokenSource? _modelsCts;
    private int _sendGuard;
    private int _pendingPersists;

    public ChatViewModel(
        ISessionCoordinator coordinator,
        StatusBarViewModel status,
        Domain.AI.IProviderRegistry providers,
        Application.Services.IUserPreferences prefs,
        Application.Services.ISessionContext sessions,
        Domain.Events.IEventBus events,
        Domain.Persistence.IChatMessageStore history,
        Diagnostics.UiFlightRecorder flight)
    {
        _coordinator = coordinator;
        _status = status;
        _providers = providers;
        _prefs = prefs;
        _sessions = sessions;
        _history = history;
        _flight = flight;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _workspacePath = prefs.WorkspacePath;
        prefs.Changed += (_, _) =>
        {
            if (_dispatcher.HasThreadAccess)
            {
                OnPreferencesChanged();
            }
            else
            {
                _dispatcher.TryEnqueue(OnPreferencesChanged);
            }
        };

        // Proveedores sin E/S (el catálogo de modelos se carga bajo demanda).
        foreach (var provider in _providers.GetAll().OrderBy(p => p.DisplayName))
        {
            AvailableProviders.Add(new ProviderOption(provider.Id, provider.DisplayName));
        }

        _suppressSelectionEvents = true;
        try
        {
            SelectedProvider = AvailableProviders.FirstOrDefault(p => p.Id == prefs.ProviderId)
                ?? AvailableProviders.FirstOrDefault();
            ShowFreeOnly = prefs.ShowFreeOnly;
            var current = new ModelOption(prefs.ProviderId, prefs.ModelId);
            _allModels.Add(current);
            AvailableModels.Add(current);
            SelectedModel = current;
            RebuildEfforts();
            if (prefs.ReasoningEffort.Length > 0)
            {
                var saved = AvailableEfforts.FirstOrDefault(e => e.Value == prefs.ReasoningEffort);
                if (saved is not null)
                {
                    SelectedEffort = saved;
                }
            }
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        SyncAuthLevelFromPrefs();
        ModelsStatus = "Elige proveedor y pulsa ↻ para listar sus modelos (incluye gratis).";
        _subscription = events.SubscribeAll(OnAgentEventAsync);
        _ = LoadHistoryAsync(CancellationToken.None);
    }

    /// <summary>Restaura el historial persistido; si no hay, mensaje de bienvenida.</summary>
    private async Task LoadHistoryAsync(CancellationToken ct)
    {
        IReadOnlyList<Domain.Persistence.StoredChatMessage> recent;
        try
        {
            recent = await _history.ListRecentAsync(MaxMessages, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            recent = Array.Empty<Domain.Persistence.StoredChatMessage>();
        }

        await RunOnUiAsync(() => ApplyHistory(recent)).ConfigureAwait(false);
    }

    /// <summary>Vuelve a la conversación en vivo (tras ver el historial).</summary>
    public Task RestoreLiveAsync(CancellationToken ct) => LoadHistoryAsync(ct);

    /// <summary>Muestra una conversación del historial (no se re-persiste).</summary>
    public async Task LoadConversationAsync(ConversationSegment segment)
    {
        var items = segment.Messages
            .Select(m => new ChatMessage(FromStoredRole(m.Role), m.Text, m.At))
            .ToList();
        await RunOnUiAsync(() =>
        {
            // No usar AppendMessage aquí: respeta _viewingHistory y al abrir una
            // segunda conversación dejaría el chat vacío (Clear + early-return).
            _viewingHistory = true;
            Messages.Clear();
            foreach (var message in items)
            {
                Messages.Add(message);
                while (Messages.Count > MaxMessages)
                {
                    Messages.RemoveAt(0);
                }
            }
        }).ConfigureAwait(false);
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Si la cola está cerrada (apagado), TryEnqueue=false: completar con error en
        // vez de dejar el await colgado para siempre.
        if (!_dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }))
        {
            done.SetException(new InvalidOperationException("UI dispatcher queue is shutting down."));
        }

        return done.Task;
    }

    private void ApplyHistory(IReadOnlyList<Domain.Persistence.StoredChatMessage> recent)
    {
        // Volvemos al vivo: la marca se fija dentro del hilo UI junto al Clear
        // para que ningún mensaje en vuelo se pierda ni contamine la vista.
        _viewingHistory = false;
        Messages.Clear();
        if (recent.Count == 0)
        {
            AppendMessage(new ChatMessage(ChatRole.System,
                "Elige la carpeta del proyecto, el proveedor y el modelo. Describe tu objetivo y pulsa Enviar (o Enter).",
                DateTimeOffset.Now), persist: false);
            return;
        }

        foreach (var stored in recent)
        {
            AppendMessage(new ChatMessage(FromStoredRole(stored.Role), stored.Text, stored.At), persist: false);
        }
    }

    /// <summary>Empieza una conversación: separador visible (también queda en el historial).</summary>
    [RelayCommand]
    private async Task NewConversationAsync(CancellationToken ct)
    {
        await RestoreLiveAsync(ct).ConfigureAwait(false);
        AddMessage(new ChatMessage(ChatRole.System, ConversationHistory.DividerText, DateTimeOffset.Now));
    }

    private static string ToStoredRole(ChatRole role) => role switch
    {
        ChatRole.User => "user",
        ChatRole.Agent => "agent",
        _ => "system",
    };

    private static ChatRole FromStoredRole(string role) => role switch
    {
        "user" => ChatRole.User,
        "agent" => ChatRole.Agent,
        _ => ChatRole.System,
    };

    private async Task PersistMessageAsync(ChatMessage message, string? sessionId)
    {
        Interlocked.Increment(ref _pendingPersists);
        try
        {
            await _history.AppendAsync(sessionId, ToStoredRole(message.Role), message.Text, message.At,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // El historial nunca debe romper el chat.
        }
        finally
        {
            Interlocked.Decrement(ref _pendingPersists);
        }
    }

    /// <summary>
    /// Espera a los guardados del historial en vuelo (tope 5 s): el apagado lo
    /// llama para no perder los últimos mensajes.
    /// </summary>
    public async Task FlushHistoryAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            while (Volatile.Read(ref _pendingPersists) > 0)
            {
                await Task.Delay(20, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        try
        {
            _modelsCts?.Cancel();
        }
        catch (Exception)
        {
        }

        _modelsCts?.Dispose();
    }

    /// <summary>El workspace se comparte (Archivos) y persiste: el setter va al store.</summary>
    partial void OnWorkspacePathChanged(string value)
    {
        if (value != _prefs.WorkspacePath && Directory.Exists(value))
        {
            _prefs.WorkspacePath = value;
        }
    }

    private void OnPreferencesChanged()
    {
        WorkspacePath = _prefs.WorkspacePath;
        SyncSelectionFromPrefs();
        SyncAuthLevelFromPrefs();
        if (ShowFreeOnly != _prefs.ShowFreeOnly)
        {
            _suppressSelectionEvents = true;
            try
            {
                ShowFreeOnly = _prefs.ShowFreeOnly;
            }
            finally
            {
                _suppressSelectionEvents = false;
            }

            RefreshSuggestions();
        }
    }

    private void SyncSelectionFromPrefs()
    {
        _suppressSelectionEvents = true;
        try
        {
            var provider = AvailableProviders.FirstOrDefault(p => p.Id == _prefs.ProviderId);
            if (provider is not null)
            {
                SelectedProvider = provider;
            }

            var current = new ModelOption(_prefs.ProviderId, _prefs.ModelId);
            if (!AvailableModels.Contains(current))
            {
                AvailableModels.Add(current);
            }

            SelectedModel = current;
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        RebuildEfforts();
        var saved = AvailableEfforts.FirstOrDefault(e => e.Value == _prefs.ReasoningEffort);
        if (saved is not null)
        {
            SelectedEffort = saved;
        }
    }

    [RelayCommand]
    private async Task LoadModelsAsync(CancellationToken ct)
    {
        if (IsLoadingModels || SelectedProvider is null)
        {
            return;
        }

        await ReloadModelsForProviderAsync(SelectedProvider.Id, ct).ConfigureAwait(true);
    }

    private async Task ReloadModelsForProviderAsync(string providerId, CancellationToken ct)
    {
        // Último-gana: un cambio rápido de proveedor cancela la carga anterior en vez
        // de descartar silenciosamente al segundo (y su respuesta lenta ya no
        // sobrescribe la selección nueva).
        _modelsCts?.Cancel();
        _modelsCts?.Dispose();
        _modelsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loadCt = _modelsCts.Token;

        IsLoadingModels = true;
        ModelsStatus = $"Cargando modelos de {providerId}…";
        try
        {
            Domain.AI.IAIProvider provider;
            try
            {
                provider = _providers.Get(providerId);
            }
            catch (KeyNotFoundException)
            {
                ModelsStatus = $"Proveedor no disponible: {providerId}.";
                return;
            }

            IReadOnlyCollection<Domain.AI.AIModel> models;
            try
            {
                models = await provider.GetModelsAsync(loadCt).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (loadCt.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Reemplazada por una carga más reciente: salir sin tocar la selección.
                return;
            }
            catch (Exception ex)
            {
                _flight.Action("models-error:" + providerId);
                ModelsStatus = $"{providerId}: {ex.Message}";
                return;
            }

            loadCt.ThrowIfCancellationRequested();

            _allModels = models
                .Select(m => new ModelOption(m.ProviderId, m.Id, m.IsFree, m.SupportedEfforts))
                .ToList();
            // "auto" siempre disponible (default del servidor; válido sobre todo en local).
            _allModels.RemoveAll(o => string.IsNullOrEmpty(o.ModelId));
            _allModels.Insert(0, new ModelOption(providerId, string.Empty));

            RefreshSuggestions();
            var keep = SelectedModel is not null
                ? AvailableModels.FirstOrDefault(o =>
                    o.ProviderId == SelectedModel.ProviderId && o.ModelId == SelectedModel.ModelId)
                : null;
            _suppressSelectionEvents = true;
            try
            {
                // Si el filtro deja la lista vacía, conservar la selección actual en vez
                // de poner null (las prefs quedarían rancias).
                SelectedModel = keep ?? AvailableModels.FirstOrDefault() ?? SelectedModel;
            }
            finally
            {
                _suppressSelectionEvents = false;
            }

            if (SelectedModel is not null && SelectedProvider is not null)
            {
                _prefs.SetModel(SelectedProvider.Id, SelectedModel.ModelId);
            }

            // El catálogo puede traer datos nuevos: el filtro de esfuerzo debe
            // reconstruirse para el modelo restaurado (SelectedModel se asignó
            // con eventos suprimidos y no dispara OnSelectedModelChanged).
            RebuildEfforts();

            var free = _allModels.Count(o => o.IsFree);
            ModelsStatus = $"{_allModels.Count} modelos en {providerId}" +
                (free > 0 ? $" ({free} gratis)." : ".");
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    /// <summary>Refiltra el desplegable (checkbox Gratis). Sin catálogo, conserva lo actual.</summary>
    public void RefreshSuggestions()
    {
        if (_allModels.Count == 0)
        {
            return;
        }

        var list = _allModels
            .Where(o => !ShowFreeOnly || o.IsFree || string.IsNullOrEmpty(o.ModelId))
            .Distinct()
            .OrderBy(o => o.Display)
            .Take(500)
            .ToList();

        AvailableModels.Clear();
        foreach (var option in list)
        {
            AvailableModels.Add(option);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken ct)
    {
        // CanSend+IsBusy no son atómicos (doble Enter rápido = doble sesión y
        // CurrentSessionId sobrescrito): guardia con Interlocked.
        if (Interlocked.CompareExchange(ref _sendGuard, 1, 0) != 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _flight.Action("send-start");
            if (_viewingHistory)
            {
                await RestoreLiveAsync(ct).ConfigureAwait(true);
            }

            // Instrucción nueva con trabajo pausado: se descarta lo pausado
            // (cancelación limpia) para que no quede un motor colgado.
            if (IsAgentPaused)
            {
                var pausedSession = _sessions.CurrentSessionId;
                if (pausedSession.HasValue)
                {
                    try
                    {
                        await _coordinator.CancelAsync(pausedSession.Value, ct).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        AddMessage(new ChatMessage(ChatRole.Agent,
                            $"No pude descartar lo pausado: {ex.Message}", DateTimeOffset.Now));
                        return;
                    }
                }

                IsAgentPaused = false;
            }

            var text = Input.Trim();
            AddMessage(new ChatMessage(ChatRole.User, text, DateTimeOffset.Now));
            Input = string.Empty;
            if (!Directory.Exists(WorkspacePath))
            {
                AddMessage(new ChatMessage(ChatRole.Agent,
                    $"La carpeta no existe: {WorkspacePath}. Selecciónala con el botón Carpeta.",
                    DateTimeOffset.Now));
                return;
            }

            if (string.IsNullOrWhiteSpace(_prefs.ModelId) && _prefs.ProviderId != "opencode")
            {
                AddMessage(new ChatMessage(ChatRole.Agent,
                    "Elige un modelo primero (Proveedor → ↻ → Modelo). Opciones sin pagar: " +
                    "modelos locales con Ollama (ver Ajustes) o los 'gratis' de OpenCode Zen " +
                    "con tu key gratuita de opencode.ai/auth (también en Ajustes).",
                    DateTimeOffset.Now));
                return;
            }

            var session = await _coordinator.StartSessionAsync(WorkspacePath, text, ct).ConfigureAwait(true);
            _status.SetSession(session);
            _sessions.CurrentSessionId = session.Id;
            var modelDisplay = _prefs.ModelId.Contains('/') ? _prefs.ModelId : $"{_prefs.ProviderId}/{_prefs.ModelId}";
            AddMessage(new ChatMessage(ChatRole.Agent,
                $"Recibido. Preparo el plan en {WorkspacePath} con {modelDisplay}…",
                DateTimeOffset.Now));

            PlanDto? plan;
            try
            {
                plan = await _coordinator.CreatePlanAsync(session.Id, text, ct).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AddMessage(new ChatMessage(ChatRole.Agent,
                    $"No pude crear el plan: {ex.Message}", DateTimeOffset.Now));
                return;
            }

            if (plan.Tasks.Count == 0)
            {
                AddMessage(new ChatMessage(ChatRole.Agent,
                    "No pude descomponer la petición en tareas. Lo habitual: falta la API key " +
                    "del proveedor. Guárdala en la pestaña Ajustes (u OPENCODE_ZEN_API_KEY / " +
                    "OPENROUTER_API_KEY) y vuelve a intentarlo. " +
                    "Puedes responder mis preguntas en la pestaña Plan igualmente.",
                    DateTimeOffset.Now));
            }
            else
            {
                AddMessage(new ChatMessage(ChatRole.Agent,
                    $"Plan listo: {plan.Tasks.Count} tareas. Revísalo en la pestaña Plan, " +
                    "responde las preguntas si hay, y pulsa Aprobar y ejecutar.",
                    DateTimeOffset.Now));
            }

            if (plan.HasOpenQuestions)
            {
                foreach (var question in plan.OpenQuestions.Take(3))
                {
                    AddMessage(new ChatMessage(ChatRole.Agent, "Pregunta: " + question.Question, DateTimeOffset.Now));
                }
            }
        }
        catch (Exception ex)
        {
            _flight.Action("send-error:" + ex.GetType().Name);
            AddMessage(new ChatMessage(ChatRole.Agent, $"Error: {ex.Message}", DateTimeOffset.Now));
        }
        finally
        {
            _flight.Action("send-end");
            IsBusy = false;
            Volatile.Write(ref _sendGuard, 0);
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanSend() => !IsBusy && _sendGuard == 0 && !string.IsNullOrWhiteSpace(Input);

    partial void OnInputChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    /// <summary>⏸ Pausa cooperativa: congela el bucle con checkpoint (reanodable).</summary>
    [RelayCommand]
    private async Task PauseAsync(CancellationToken ct)
    {
        var sessionId = _sessions.CurrentSessionId;
        if (!IsAgentWorking || IsAgentPaused || sessionId is null)
        {
            return;
        }

        try
        {
            await _coordinator.PauseAsync(sessionId.Value, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage(ChatRole.Agent,
                $"No pude pausar: {ex.Message}", DateTimeOffset.Now));
        }
    }

    /// <summary>▶ Reanuda la sesión pausada y continúa sus tareas pendientes.</summary>
    [RelayCommand]
    private async Task ResumeAsync(CancellationToken ct)
    {
        var sessionId = _sessions.CurrentSessionId;
        if (!IsAgentPaused || sessionId is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _coordinator.ResumeAsync(sessionId.Value, ct).ConfigureAwait(true);
            AddMessage(new ChatMessage(ChatRole.Agent,
                "Reanudando donde lo dejamos…", DateTimeOffset.Now));
            await Task.Run(() => _coordinator.RunAsync(sessionId.Value, CancellationToken.None), ct)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AddMessage(new ChatMessage(ChatRole.Agent,
                "Reanudación cancelada.", DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage(ChatRole.Agent,
                $"No pude reanudar: {ex.Message}", DateTimeOffset.Now));
        }
        finally
        {
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>⏹ Detiene del todo: cancela el motor y libera la sesión.</summary>
    [RelayCommand]
    private async Task StopAsync(CancellationToken ct)
    {
        var sessionId = _sessions.CurrentSessionId;
        if ((!IsAgentWorking && !IsAgentPaused) || sessionId is null)
        {
            return;
        }

        try
        {
            await _coordinator.CancelAsync(sessionId.Value, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage(ChatRole.Agent,
                $"No pude detener: {ex.Message}", DateTimeOffset.Now));
            return;
        }

        IsAgentPaused = false;
    }

    private void AddMessage(ChatMessage message)
    {
        if (_dispatcher.HasThreadAccess)
        {
            AppendMessage(message);
        }
        else
        {
            _dispatcher.TryEnqueue(() => AppendMessage(message));
        }
    }

    private void AppendMessage(ChatMessage message, bool persist = true)
    {
        if (persist)
        {
            _ = PersistMessageAsync(message, _sessions.CurrentSessionId?.ToString("N"));
        }

        // Viendo historial: se guarda todo, pero la vista no se contamina.
        if (_viewingHistory)
        {
            return;
        }

        Messages.Add(message);
        while (Messages.Count > MaxMessages)
        {
            Messages.RemoveAt(0);
        }
    }

    /// <summary>Narración del agente en el chat + línea de actividad en vivo.</summary>
    private Task OnAgentEventAsync(Domain.Events.AgentEvent e, CancellationToken ct)
    {
        // Pausa/stop propios: el flag gobierna los botones ⏸/▶/⏹.
        if (e.Type == AgentEventType.AgentPaused)
        {
            SetPaused(true);
            return Task.CompletedTask;
        }

        if (e.Type is AgentEventType.AgentResumed or AgentEventType.AgentStopped)
        {
            SetPaused(false);
            if (e.Type == AgentEventType.AgentStopped)
            {
                return Task.CompletedTask;
            }
        }

        if (e.Type == AgentEventType.ToolStarted)
        {
            // Resumen "Id started." + args en PayloadJson (lo pone el ejecutor).
            var summary = e.Summary ?? string.Empty;
            var toolId = summary.EndsWith(" started.", StringComparison.Ordinal)
                ? summary[..^" started.".Length] : summary;
            SetActivity(AgentActivityText.ForToolStarted(toolId, e.PayloadJson));
            return Task.CompletedTask;
        }

        if (e.Type == AgentEventType.ToolCompleted)
        {
            SetActivity("Pensando…");
            var narrative = ToolNarrative(e.Summary, e.PayloadJson);
            if (narrative is not null)
            {
                AddMessage(new ChatMessage(ChatRole.Agent, narrative, e.OccurredAt));
            }

            return Task.CompletedTask;
        }

        if (e.Type == AgentEventType.RepairStarted)
        {
            SetActivity("Solucionando…");
            return Task.CompletedTask;
        }

        if (e.Type == AgentEventType.StateChanged)
        {
            var summary = e.Summary ?? string.Empty;
            if (summary.Contains("Completed") || summary.Contains("Failed")
                || summary.Contains("Cancelled"))
            {
                SetPaused(false); // terminal: no hay nada que reanudar
            }

            var to = ParseStateTo(e.Summary);
            var activity = AgentActivityText.ForState(to);
            if (activity is not null)
            {
                SetActivity(activity);
            }
        }

        string? text = e.Type switch
        {
            AgentEventType.TaskStarted => $"Empezando tarea: {e.Summary}",
            AgentEventType.TaskCompleted => $"Tarea hecha: {e.Summary}",
            AgentEventType.TaskFailed => $"Tarea fallida: {e.Summary}",
            AgentEventType.StateChanged when (e.Summary ?? string.Empty).Contains("Completed") =>
                ClearAnd("Trabajo completado y auditado."),
            AgentEventType.StateChanged when (e.Summary ?? string.Empty).Contains("Failed") =>
                ClearAnd("La ejecución falló. Mira la pestaña Salida para el detalle."),
            AgentEventType.StateChanged when (e.Summary ?? string.Empty).Contains("Cancelled") =>
                ClearAnd("Ejecución cancelada. Puedes darme otra instrucción cuando quieras."),
            AgentEventType.StateChanged when (e.Summary ?? string.Empty).Contains("Paused") =>
                ClearAnd("Ejecución en pausa. Pulsa Reanudar para continuar, o escribe una instrucción y Envía (se descarta lo pausado)."),
            _ => null,
        };
        if (text is not null)
        {
            AddMessage(new ChatMessage(ChatRole.Agent, text, e.OccurredAt));
        }

        return Task.CompletedTask;
    }

    private string ClearAnd(string text)
    {
        ClearActivity();
        return text;
    }

    /// <summary>
    /// Resumen "Id: ok|error" + args en PayloadJson → burbuja de historial
    /// (qué archivo editó / qué comando ejecutó). Null si no narrable.
    /// </summary>
    private static string? ToolNarrative(string? summary, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var cut = summary.IndexOf(':');
        if (cut <= 0)
        {
            return null;
        }

        var toolId = summary[..cut].Trim();
        var ok = summary.TrimEnd().EndsWith(": ok", StringComparison.Ordinal);
        var error = ok ? null : summary[(cut + 1)..].Trim();
        return AgentActivityText.ForToolCompleted(toolId, argumentsJson, ok, error);
    }

    private static string? ParseStateTo(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var arrow = summary.IndexOf("->", StringComparison.Ordinal);
        if (arrow < 0)
        {
            return null;
        }

        var rest = summary[(arrow + 2)..].Trim();
        var colon = rest.IndexOf(':');
        return (colon < 0 ? rest : rest[..colon]).Trim();
    }
}

/// <summary>Panel del plan: preguntas, tareas, aprobar+ejecutar y cancelar.</summary>
public sealed partial class PlanViewModel : ObservableObject, IDisposable
{
    private readonly ISessionCoordinator _coordinator;
    private readonly Application.Services.ISessionContext _sessions;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable _eventsSubscription;
    private CancellationTokenSource? _runCts;
    private readonly object _runCtsLock = new();
    private int _planRefreshGen;

    [ObservableProperty]
    private PlanDto? _plan;

    /// <summary>Tareas null-safe para binding (Plan puede ser null antes de cargar).</summary>
    public IReadOnlyList<TaskDto> PlanTasks => Plan?.Tasks ?? Array.Empty<TaskDto>();

    public IReadOnlyList<OpenQuestionDto> OpenQuestions => Plan?.OpenQuestions ?? Array.Empty<OpenQuestionDto>();

    public bool HasQuestions => OpenQuestions.Count > 0;

    public Visibility HasQuestionsVisibility =>
        HasQuestions ? Visibility.Visible : Visibility.Collapsed;

    partial void OnPlanChanged(PlanDto? value)
    {
        OnPropertyChanged(nameof(PlanTasks));
        OnPropertyChanged(nameof(OpenQuestions));
        OnPropertyChanged(nameof(HasQuestions));
        OnPropertyChanged(nameof(HasQuestionsVisibility));
        if (SelectedQuestion is null || SelectedQuestion.IsAnswered)
        {
            SelectedQuestion = value?.OpenQuestions.FirstOrDefault(q => !q.IsAnswered);
        }

        ApproveAndRunCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private OpenQuestionDto? _selectedQuestion;

    /// <summary>Opciones sugeridas + "Otro" libre al final.</summary>
    public IReadOnlyList<string> CurrentOptions =>
        SelectedQuestion is null
            ? Array.Empty<string>()
            : SelectedQuestion.SuggestedOptions.Concat(new[] { OtherOption }).Distinct().ToList();

    public const string OtherOption = "Otro (escribir abajo)";

    [ObservableProperty]
    private string? _selectedOption;

    partial void OnSelectedQuestionChanged(OpenQuestionDto? value)
    {
        OnPropertyChanged(nameof(CurrentOptions));
        SelectedOption = null;
        AnswerCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedOptionChanged(string? value) => AnswerCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    private string _answerText = string.Empty;

    partial void OnAnswerTextChanged(string value) => AnswerCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    partial void OnIsRunningChanged(bool value)
    {
        ApproveAndRunCommand.NotifyCanExecuteChanged();
        CancelRunCommand.NotifyCanExecuteChanged();
    }

    public PlanViewModel(
        ISessionCoordinator coordinator,
        Application.Services.ISessionContext sessions,
        Domain.Events.IEventBus events,
        Diagnostics.UiFlightRecorder flight)
    {
        _statusText = "Sin plan cargado.";
        _coordinator = coordinator;
        _sessions = sessions;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _sessions.Changed += (_, _) => _ = RefreshForCurrentSessionAsync();
        _eventsSubscription = events.SubscribeAll(OnPlanEventAsync);
        _ = RefreshForCurrentSessionAsync();
    }

    public void Dispose()
    {
        _eventsSubscription.Dispose();
        lock (_runCtsLock)
        {
            try
            {
                _runCts?.Cancel();
            }
            catch (Exception)
            {
            }

            _runCts?.Dispose();
            _runCts = null;
        }
    }

    /// <summary>Refresca cuando el plan avanza sin estar mirando (creación, tareas, estados).</summary>
    private Task OnPlanEventAsync(Domain.Events.AgentEvent e, CancellationToken ct)
    {
        if (e.Type is AgentEventType.PlanCreated or AgentEventType.TaskCompleted
            or AgentEventType.TaskFailed or AgentEventType.StateChanged)
        {
            _ = RefreshForCurrentSessionAsync();
        }

        return Task.CompletedTask;
    }

    private async Task RefreshForCurrentSessionAsync()
    {
        // Generación: N eventos solapados (PlanCreated/TaskCompleted/…) lanzan N
        // recargas; solo la última pinta para evitar parpadeo y estados viejos.
        var gen = Interlocked.Increment(ref _planRefreshGen);
        var sessionId = _sessions.CurrentSessionId;
        if (sessionId is null)
        {
            SetPlan(null, "Sin sesión. Pide algo en el Chat primero.");
            return;
        }

        try
        {
            var plan = await _coordinator.GetPlanAsync(sessionId.Value, CancellationToken.None)
                .ConfigureAwait(true);
            if (Volatile.Read(ref _planRefreshGen) == gen)
            {
                SetPlan(plan, null);
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _planRefreshGen) == gen)
            {
                SetPlan(null, $"Error cargando el plan: {ex.Message}");
            }
        }
    }

    private void SetPlan(PlanDto? plan, string? status)
    {
        _dispatcher.TryEnqueue(() =>
        {
            Plan = plan;
            StatusText = status ?? (plan is null
                ? "La sesión aún no tiene plan."
                : $"Plan rev {plan.Revision} · {plan.Status} · {plan.Tasks.Count} tareas");
        });
    }

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private async Task AnswerAsync(CancellationToken ct)
    {
        if (Plan is null || SelectedQuestion is null)
        {
            return;
        }

        // Opción elegida, u "Otro" con texto libre.
        var answer = SelectedOption is null || SelectedOption == OtherOption
            ? AnswerText.Trim()
            : SelectedOption;
        if (string.IsNullOrWhiteSpace(answer))
        {
            return;
        }

        try
        {
            var updated = await _coordinator.AnswerQuestionAsync(
                Plan.Id, SelectedQuestion.Id, answer, ct).ConfigureAwait(true);
            AnswerText = string.Empty;
            SelectedOption = null;
            SetPlan(updated, null);
        }
        catch (Exception ex)
        {
            StatusText = $"Error respondiendo: {ex.Message}";
        }
    }

    private bool CanAnswer() => Plan is not null
        && SelectedQuestion is not null && !SelectedQuestion.IsAnswered
        && (!string.IsNullOrWhiteSpace(AnswerText)
            || (SelectedOption is not null && SelectedOption != OtherOption))
        && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanApproveAndRun))]
    private async Task ApproveAndRunAsync(CancellationToken ct)
    {
        if (Plan is null || _sessions.CurrentSessionId is null)
        {
            return;
        }

        try
        {
            var approved = await _coordinator.ApprovePlanAsync(Plan.Id, ct).ConfigureAwait(true);
            SetPlan(approved, null);
        }
        catch (Exception ex)
        {
            StatusText = $"No se pudo aprobar: {ex.Message}";
            return;
        }

        CancellationTokenSource runCts;
        lock (_runCtsLock)
        {
            // Cancelar+disponer el anterior bajo lock: sin esto, CancelRun concurrente
            // podía lanzar ObjectDisposedException o cancelar el CTS viejo en vez del nuevo.
            try
            {
                _runCts?.Cancel();
            }
            catch (Exception)
            {
            }

            _runCts?.Dispose();
            runCts = new CancellationTokenSource();
            _runCts = runCts;
        }

        var sessionId = _sessions.CurrentSessionId.Value;
        IsRunning = true;
        StatusText = "Ejecutando… (ver progreso en Chat y Salida)";
        try
        {
            // ConfigureAwait(true): este comando vive en UI y StatusText/IsRunning son
            // bindings; el trabajo pesado ya corre en Task.Run.
            await Task.Run(() => _coordinator.RunAsync(sessionId, runCts.Token), runCts.Token)
                .ConfigureAwait(true);
            var finished = await _coordinator.GetPlanAsync(sessionId, CancellationToken.None)
                .ConfigureAwait(true);
            SetPlan(finished, null);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Ejecución cancelada.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error ejecutando: {ex.Message}";
        }
        finally
        {
            lock (_runCtsLock)
            {
                if (ReferenceEquals(_runCts, runCts))
                {
                    _runCts = null;
                }
            }

            runCts.Dispose();
            IsRunning = false;
        }
    }

    private bool CanApproveAndRun() => Plan?.Status == PlanStatus.InReview
        && Plan?.HasOpenQuestions == false && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancelRun))]
    private async Task CancelRunAsync(CancellationToken ct)
    {
        if (_sessions.CurrentSessionId is null)
        {
            return;
        }

        try
        {
            CancellationTokenSource? current;
            lock (_runCtsLock)
            {
                current = _runCts;
            }

            try
            {
                current?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            await _coordinator.CancelAsync(_sessions.CurrentSessionId.Value, CancellationToken.None)
                .ConfigureAwait(true);
            StatusText = "Ejecución cancelada.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error cancelando: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanCancelRun() => IsRunning;
}

/// <summary>Panel de tareas del plan activo (sigue a la sesión actual).</summary>
public sealed partial class TasksViewModel : ObservableObject, IDisposable
{
    private readonly ISessionCoordinator _coordinator;
    private readonly Application.Services.ISessionContext _sessions;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable _subscription;
    private int _reloadGen;

    public ObservableCollection<TaskDto> Tasks { get; } = new();

    public TasksViewModel(
        ISessionCoordinator coordinator,
        Application.Services.ISessionContext sessions,
        Domain.Events.IEventBus events)
    {
        _coordinator = coordinator;
        _sessions = sessions;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _sessions.Changed += (_, _) => _ = ReloadAsync();
        _subscription = events.SubscribeAll(OnTasksEventAsync);
        _ = ReloadAsync();
    }

    public void Dispose() => _subscription.Dispose();

    private Task OnTasksEventAsync(Domain.Events.AgentEvent e, CancellationToken ct)
    {
        if (e.Type is AgentEventType.PlanCreated or AgentEventType.TaskCompleted
            or AgentEventType.TaskFailed or AgentEventType.StateChanged)
        {
            _ = ReloadAsync();
        }

        return Task.CompletedTask;
    }

    private async Task ReloadAsync()
    {
        var gen = Interlocked.Increment(ref _reloadGen);
        var sessionId = _sessions.CurrentSessionId;
        if (sessionId is null)
        {
            _dispatcher.TryEnqueue(Tasks.Clear);
            return;
        }

        try
        {
            var plan = await _coordinator.GetPlanAsync(sessionId.Value, CancellationToken.None)
                .ConfigureAwait(false);
            if (Volatile.Read(ref _reloadGen) != gen)
            {
                return; // una recarga más reciente ganó
            }

            _dispatcher.TryEnqueue(() =>
            {
                Tasks.Clear();
                if (plan is not null)
                {
                    foreach (var t in plan.Tasks)
                    {
                        Tasks.Add(t);
                    }
                }
            });
        }
        catch (Exception)
        {
        }
    }

    [RelayCommand]
    private async Task LoadAsync(Guid sessionId, CancellationToken ct)
    {
        _sessions.CurrentSessionId = sessionId;
        await ReloadAsync().ConfigureAwait(true);
    }
}

/// <summary>Panel de archivos: explora el workspace compartido (lectura).</summary>
public sealed partial class FilesViewModel : ObservableObject
{
    private readonly Domain.Context.IContextBuilder _context;
    private readonly Application.Services.IUserPreferences _prefs;
    private readonly DispatcherQueue _dispatcher;
    private readonly Diagnostics.UiFlightRecorder _flight; // TEMPORARY-DIAGNOSTIC
    private CancellationTokenSource? _refreshCts;

    private ObservableCollection<Microsoft.UI.Xaml.Controls.TreeViewNode> _fileTree = new();
    public ObservableCollection<Microsoft.UI.Xaml.Controls.TreeViewNode> FileTree
    {
        get => _fileTree;
        private set => SetProperty(ref _fileTree, value);
    }

    public string WorkspacePath => _prefs.WorkspacePath;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public FilesViewModel(
        Domain.Context.IContextBuilder context,
        Application.Services.IUserPreferences prefs,
        Diagnostics.UiFlightRecorder flight)
    {
        _context = context;
        _prefs = prefs;
        _flight = flight;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _prefs.Changed += (_, _) =>
        {
            if (_dispatcher.HasThreadAccess)
            {
                OnPreferencesChanged();
            }
            else
            {
                _dispatcher.TryEnqueue(OnPreferencesChanged);
            }
        };
        _ = RefreshAsync();
    }

    private void OnPreferencesChanged()
    {
        OnPropertyChanged(nameof(WorkspacePath));
        _ = RefreshAsync();
    }

    private Task RefreshAsync() => RefreshCommand.ExecuteAsync(null);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken ct)
    {
        // Último-gana con cancelación real: las ráfagas de prefs.Changed cancelan la
        // exploración anterior en vez de descartarse (o apilarse).
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loadCt = _refreshCts.Token;

        IsRefreshing = true;
        RefreshCommand.NotifyCanExecuteChanged();
        StatusText = "Explorando…";
        _flight.Action("refresh-start:" + WorkspacePath);
        try
        {
            // Disco en background: el hilo UI nunca espera E/S (aunque el lector ya es
            // cancelable y acotado, la enumeración no debe correr aquí).
            var workspace = WorkspacePath;
            var context = await Task.Run(
                () => _context.BuildAsync(workspace, Array.Empty<string>(), 5_000_000, loadCt), loadCt)
                .ConfigureAwait(false);
            loadCt.ThrowIfCancellationRequested();

            // Árbol puro en background (sin UI): anida, ordena y cuenta.
            var tree = await Task.Run(
                () => Application.Services.FileTreeBuilder.Build(
                    workspace, context.Fragments.Select(f => f.Source)), loadCt)
                .ConfigureAwait(false);

            await RunOnUiAsync(() =>
            {
                FileTree = MapToNodes(tree);
                FileCount = tree.Item.DescendantFiles;
                StatusText = $"{FileCount} archivos.";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _flight.Action("refresh-cancelled");
            await RunOnUiAsync(() => { StatusText = "Cancelado."; }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _flight.Action("refresh-error:" + ex.GetType().Name);
            var message = ex.Message;
            await RunOnUiAsync(() => { StatusText = $"Error: {message}"; }).ConfigureAwait(false);
        }
        finally
        {
            _flight.Action("refresh-end");
            await RunOnUiAsync(() =>
            {
                IsRefreshing = false;
                RefreshCommand.NotifyCanExecuteChanged();
            }).ConfigureAwait(false);
        }
    }

    private static ObservableCollection<Microsoft.UI.Xaml.Controls.TreeViewNode> MapToNodes(
        Application.Services.FileTreeNode root)
    {
        var nodes = new ObservableCollection<Microsoft.UI.Xaml.Controls.TreeViewNode>
        {
            MapNode(root, depth: 0),
        };
        return nodes;
    }

    private static Microsoft.UI.Xaml.Controls.TreeViewNode MapNode(
        Application.Services.FileTreeNode node, int depth)
    {
        // Raíz y primer nivel abiertos: se ve la estructura sin clicks.
        var treeNode = new Microsoft.UI.Xaml.Controls.TreeViewNode
        {
            Content = node.Item,
            IsExpanded = depth <= 1,
        };
        foreach (var child in node.Children)
        {
            treeNode.Children.Add(MapNode(child, depth + 1));
        }

        return treeNode;
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }))
        {
            done.SetException(new InvalidOperationException("UI dispatcher queue is shutting down."));
        }

        return done.Task;
    }

    private bool CanRefresh() => !IsRefreshing;
}

/// <summary>Panel de salida: log de ejecución de la sesión + eventos recientes.</summary>
public sealed partial class OutputViewModel : ObservableObject
{
    private readonly Domain.Persistence.ICheckpointStore _checkpoints;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable _subscription;
    private readonly object _pendingLock = new();
    private readonly List<string> _pending = new();
    private bool _flushScheduled;

    public ObservableCollection<string> Lines { get; } = new();

    public OutputViewModel(Domain.Persistence.ICheckpointStore checkpoints, Domain.Events.IEventBus events)
    {
        _checkpoints = checkpoints;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _subscription = events.SubscribeAll(OnEventAsync);
    }

    private Task OnEventAsync(Domain.Events.AgentEvent e, CancellationToken ct)
    {
        // Coalescar: cada evento encolaba un pump UI con RemoveAt(0) O(n).
        // Se acumula en buffer y se vacía en un solo pase por lote de eventos.
        lock (_pendingLock)
        {
            _pending.Add($"[{e.OccurredAt:HH:mm:ss}] {e.Type}: {e.Summary}");
            if (_flushScheduled)
            {
                return Task.CompletedTask;
            }

            _flushScheduled = true;
        }

        if (!_dispatcher.TryEnqueue(FlushPending))
        {
            lock (_pendingLock)
            {
                _flushScheduled = false;
            }
        }

        return Task.CompletedTask;
    }

    private void FlushPending()
    {
        List<string> batch;
        lock (_pendingLock)
        {
            _flushScheduled = false;
            if (_pending.Count == 0)
            {
                return;
            }

            batch = new List<string>(_pending);
            _pending.Clear();
        }

        foreach (var line in batch)
        {
            Lines.Add(line);
        }

        // Ventana de 500: recorte en bloque en vez de N RemoveAt(0).
        var overflow = Lines.Count - 500;
        for (var i = 0; i < overflow; i++)
        {
            Lines.RemoveAt(0);
        }
    }

    [RelayCommand]
    private async Task LoadLogAsync(Guid sessionId, CancellationToken ct)
    {
        var entries = await _checkpoints.ReadExecutionLogAsync(sessionId, ct).ConfigureAwait(true);
        Lines.Clear();
        foreach (var e in entries)
        {
            Lines.Add($"[{e.OccurredAt:HH:mm:ss}] {e.Category}: {e.Message}");
        }
    }
}

/// <summary>Historial de conversaciones: lista lo guardado, abre en el Chat y vuelve al vivo.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly Domain.Persistence.IChatMessageStore _history;
    private readonly ChatViewModel _chat;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<ConversationSegment> Conversations { get; } = new();

    [ObservableProperty]
    private ConversationSegment? _selectedConversation;

    [ObservableProperty]
    private string _statusText = "Cargando conversaciones guardadas…";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Apertura en curso (independiente de <see cref="IsBusy"/> para que
    /// abrir una conversación nunca se descarte por estar refrescando la lista).</summary>
    private bool _isOpening;
    private bool _refreshPending;

    public HistoryViewModel(Domain.Persistence.IChatMessageStore history, ChatViewModel chat)
    {
        _history = history;
        _chat = chat;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = RefreshAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        // Si el refresh del ctor sigue en curso cuando la vista pide otro (Loaded en
        // cada navegación), no perderlo: marcar pendiente y re-ejecutar al terminar.
        if (IsBusy)
        {
            _refreshPending = true;
            return;
        }

        IsBusy = true; // viene del hilo UI (botón/ctor)
        do
        {
            _refreshPending = false;
            IReadOnlyList<ConversationSegment>? segments = null;
            string? error = null;
            try
            {
                var messages = await _history.ListRecentAsync(1000, ct).ConfigureAwait(false);
                segments = ConversationHistory.Segment(messages);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            var capturedSegments = segments;
            var capturedError = error;
            await RunOnUiAsync(() =>
            {
                Conversations.Clear();
                if (capturedSegments is not null)
                {
                    foreach (var segment in capturedSegments)
                    {
                        Conversations.Add(segment);
                    }
                }

                StatusText = capturedError is not null
                    ? $"No se pudo leer el historial: {capturedError}"
                    : capturedSegments!.Count == 0
                        ? "Aún no hay conversaciones guardadas."
                        : $"{capturedSegments.Count} conversaciones guardadas (la más reciente arriba).";
            }).ConfigureAwait(false);
        }
        while (_refreshPending && !ct.IsCancellationRequested);

        await RunOnUiAsync(() => { IsBusy = false; }).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task OpenAsync(ConversationSegment? segment, CancellationToken ct)
    {
        if (segment is null || _isOpening)
        {
            return;
        }

        _isOpening = true;
        try
        {
            await _chat.LoadConversationAsync(segment).ConfigureAwait(false);
        }
        finally
        {
            _isOpening = false;
        }
    }

    [RelayCommand]
    private async Task OpenLiveAsync(CancellationToken ct)
    {
        if (_isOpening)
        {
            return;
        }

        _isOpening = true;
        try
        {
            await _chat.RestoreLiveAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _isOpening = false;
        }
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }))
        {
            done.SetException(new InvalidOperationException("UI dispatcher queue is shutting down."));
        }

        return done.Task;
    }
}
