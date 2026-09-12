// CA-A-IA — Pestaña Autonomía: como el Chat, pero el agente controla tu PC
// como un humano (ratón/teclado reales) con carpeta temporal de trabajo.
// Cada instrucción = sesión nueva en %TEMP%\CA-A-IA-autonomy\<id>.
//
// NOTA (MVVMTK0045): ver ViewModels.cs (campos con [ObservableProperty], sin AOT/trimming).
#pragma warning disable MVVMTK0045

using System.Collections.ObjectModel;
using CaAIA.Application.Services;
using CaAIA.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CaAIA.Presentation.ViewModels;

/// <summary>
/// Autonomía: instrucciones en lenguaje natural → el agente mueve el ratón,
/// escribe y ejecuta como lo harías tú (movimiento humanizado). Sus archivos
/// viven en una carpeta temporal por instrucción.
/// </summary>
public sealed partial class AutonomyViewModel : ObservableObject, IDisposable
{
    private const int MaxMessages = 200;
    private static readonly TimeSpan PruneTempAfter = TimeSpan.FromDays(7);

    private readonly ISessionCoordinator _coordinator;
    private readonly StatusBarViewModel _status;
    private readonly Application.Services.IUserPreferences _prefs;
    private readonly Application.Services.ISessionContext _sessions;
    private readonly Domain.Persistence.IChatMessageStore _history;
    private readonly Application.Services.LessonStore _lessons;
    private readonly DispatcherQueue _dispatcher;
    private readonly IDisposable _subscription;
    private readonly object _runCtsLock = new();

    private CancellationTokenSource? _runCts;
    private Guid? _autonomySessionId;
    private int _sendGuard;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private string _agentStatus = string.Empty;

    [ObservableProperty]
    private bool _isAgentWorking;

    [ObservableProperty]
    private bool _isAgentPaused;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _workspacePath = string.Empty;

    public Visibility AgentStatusVisibility =>
        IsAgentWorking || !string.IsNullOrWhiteSpace(AgentStatus)
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PauseButtonVisibility =>
        IsAgentWorking && !IsAgentPaused ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ResumeButtonVisibility =>
        IsAgentPaused ? Visibility.Visible : Visibility.Collapsed;

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

    public AutonomyViewModel(
        ISessionCoordinator coordinator,
        StatusBarViewModel status,
        Application.Services.IUserPreferences prefs,
        Application.Services.ISessionContext sessions,
        Domain.Events.IEventBus events,
        Domain.Persistence.IChatMessageStore history,
        Application.Services.LessonStore lessons,
        Diagnostics.UiFlightRecorder flight)
    {
        _coordinator = coordinator;
        _status = status;
        _prefs = prefs;
        _sessions = sessions;
        _history = history;
        _lessons = lessons;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _subscription = events.SubscribeAll(OnAgentEventAsync);
        _ = Task.Run(PruneOldTempDirs);
        AddMessage(new ChatMessage(ChatRole.System,
            "Pide lo que sea: «abre el navegador y busca…», «organiza mis descargas»… " +
            "Tres trabajan para ti: uno mueve el ratón y escribe, otro mira la pantalla " +
            "tras cada acción y el modelo lo analiza todo antes de seguir. Usa un modelo " +
            "rápido y gratuito para volar. Cada pedido usa su propia carpeta temporal. " +
            "Si me equivoco, dime «recuerda: …» y no lo repetiré.",
            DateTimeOffset.Now), persist: false);
    }

    public void Dispose()
    {
        _subscription.Dispose();
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

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _sendGuard, 1, 0) != 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var text = Input.Trim();
            AddMessage(new ChatMessage(ChatRole.User, text, DateTimeOffset.Now));
            Input = string.Empty;

            // "recuerda: ..." = lección explícita (proyecto de Carpeta o global).
            if (Application.Services.LessonIntake.TryExtract(text, out var lessonScope, out var lessonText))
            {
                var where = lessonScope == Application.Services.LessonScope.Global
                    ? null : _prefs.WorkspacePath;
                if (lessonScope == Application.Services.LessonScope.Project && !Directory.Exists(_prefs.WorkspacePath))
                {
                    AddMessage(new ChatMessage(ChatRole.Agent,
                        "Para recordar en un proyecto, elige primero la carpeta en el Chat.", DateTimeOffset.Now));
                }
                else
                {
                    try
                    {
                        await _lessons.RecordLessonAsync(where, lessonScope, lessonText, "usuario", ct)
                            .ConfigureAwait(true);
                        AddMessage(new ChatMessage(ChatRole.Agent,
                            "Anotado ✓ Lo aplicaré de ahora en adelante.", DateTimeOffset.Now));
                    }
                    catch (Exception ex)
                    {
                        AddMessage(new ChatMessage(ChatRole.Agent,
                            $"No pude anotarlo: {ex.Message}", DateTimeOffset.Now));
                    }
                }

                return;
            }

            // Si había algo pausado, se descarta limpio antes de lo nuevo.
            if (IsAgentPaused)
            {
                await DiscardPausedAsync(ct).ConfigureAwait(true);
            }

            var workspace = PrepareTempWorkspace();
            WorkspacePath = workspace;

            var session = await _coordinator.StartSessionAsync(workspace, text, ct).ConfigureAwait(true);
            _autonomySessionId = session.Id;
            _sessions.CurrentSessionId = session.Id;
            _status.SetSession(session);
            AddMessage(new ChatMessage(ChatRole.Agent,
                $"Recibido. Preparo el plan en una carpeta temporal nueva…", DateTimeOffset.Now));

            Application.DTOs.PlanDto? plan;
            try
            {
                plan = await _coordinator.CreatePlanAsync(session.Id, AutonomyBrief(text), ct)
                    .ConfigureAwait(true);
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
                    "No pude descomponer la petición. Lo habitual: falta la API key " +
                    "(Ajustes) o el proveedor no responde.", DateTimeOffset.Now));
                return;
            }

            if (plan.HasOpenQuestions)
            {
                AddMessage(new ChatMessage(ChatRole.Agent,
                    $"Tengo {plan.OpenQuestions.Count} pregunta(s) antes de tocar tu PC: " +
                    "respóndelas en la pestaña Plan y aprueba allí.", DateTimeOffset.Now));
                foreach (var question in plan.OpenQuestions.Take(3))
                {
                    AddMessage(new ChatMessage(ChatRole.Agent, "Pregunta: " + question.Question, DateTimeOffset.Now));
                }

                return;
            }

            await ApproveAndRunAsync(plan.Id, session.Id, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage(ChatRole.Agent, $"Error: {ex.Message}", DateTimeOffset.Now));
        }
        finally
        {
            Volatile.Write(ref _sendGuard, 0);
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanSend() => !IsBusy && _sendGuard == 0 && !string.IsNullOrWhiteSpace(Input);

    partial void OnInputChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    /// <summary>Encuadre de autonomía para el planificador (el ejecutor ya sabe
    /// de las herramientas UI por su system prompt).</summary>
    internal static string AutonomyBrief(string instruction) =>
        $"[AUTONOMÍA — control del PC como un humano] {instruction} " +
        "(Usa UiGetScreen para la resolución; mueve el ratón a elementos visibles reales; " +
        "archivos que crees, dentro del workspace con rutas absolutas.)";

    private async Task ApproveAndRunAsync(Guid planId, Guid sessionId, CancellationToken ct)
    {
        Application.DTOs.PlanDto approved;
        try
        {
            approved = await _coordinator.ApprovePlanAsync(planId, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage(ChatRole.Agent,
                $"No se pudo aprobar: {ex.Message}", DateTimeOffset.Now));
            return;
        }

        CancellationTokenSource runCts;
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
            runCts = new CancellationTokenSource();
            _runCts = runCts;
        }

        AddMessage(new ChatMessage(ChatRole.Agent,
            $"Plan listo: {approved.Tasks.Count} tareas. Manos a la obra: verás cada movimiento aquí.",
            DateTimeOffset.Now));
        try
        {
            await Task.Run(() => _coordinator.RunAsync(sessionId, runCts.Token), runCts.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AddMessage(new ChatMessage(ChatRole.Agent, "Ejecución cancelada.", DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage(ChatRole.Agent, $"Error ejecutando: {ex.Message}", DateTimeOffset.Now));
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
        }
    }

    [RelayCommand]
    private async Task PauseAsync(CancellationToken ct)
    {
        if (!IsAgentWorking || IsAgentPaused || _autonomySessionId is null)
        {
            return;
        }

        try
        {
            await _coordinator.PauseAsync(_autonomySessionId.Value, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage(ChatRole.Agent, $"No pude pausar: {ex.Message}", DateTimeOffset.Now));
        }
    }

    [RelayCommand]
    private async Task ResumeAsync(CancellationToken ct)
    {
        if (!IsAgentPaused || _autonomySessionId is null || IsBusy)
        {
            return;
        }

        var sessionId = _autonomySessionId.Value;
        IsBusy = true;
        try
        {
            await _coordinator.ResumeAsync(sessionId, ct).ConfigureAwait(true);
            AddMessage(new ChatMessage(ChatRole.Agent, "Reanudando donde lo dejamos…", DateTimeOffset.Now));
            await Task.Run(() => _coordinator.RunAsync(sessionId, CancellationToken.None), ct)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AddMessage(new ChatMessage(ChatRole.Agent, "Reanudación cancelada.", DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage(ChatRole.Agent, $"No pude reanudar: {ex.Message}", DateTimeOffset.Now));
        }
        finally
        {
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task StopAsync(CancellationToken ct)
    {
        if ((!IsAgentWorking && !IsAgentPaused) || _autonomySessionId is null)
        {
            return;
        }

        try
        {
            await _coordinator.CancelAsync(_autonomySessionId.Value, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage(ChatRole.Agent, $"No pude detener: {ex.Message}", DateTimeOffset.Now));
            return;
        }

        IsAgentPaused = false;
    }

    /// <summary>
    /// 🧭 Seguir sin finalizar: reencola lo a medias con tu instrucción y continúa.
    /// </summary>
    [RelayCommand]
    private async Task NudgeAsync(CancellationToken ct)
    {
        if ((!IsAgentWorking && !IsAgentPaused) || _autonomySessionId is null || IsBusy)
        {
            return;
        }

        var sessionId = _autonomySessionId.Value;
        var instruction = string.IsNullOrWhiteSpace(Input)
            ? "Sigue con la tarea, no te quedes atascado: prueba otro enfoque y continúa."
            : Input.Trim();
        if (!string.IsNullOrWhiteSpace(Input))
        {
            Input = string.Empty;
        }

        IsBusy = true;
        try
        {
            await _coordinator.NudgeAsync(sessionId, instruction, ct).ConfigureAwait(true);
            AddMessage(new ChatMessage(ChatRole.System, "🧭 " + instruction, DateTimeOffset.Now));
            await Task.Run(() => _coordinator.RunAsync(sessionId, CancellationToken.None), ct)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AddMessage(new ChatMessage(ChatRole.Agent, "Seguimiento cancelado.", DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage(ChatRole.Agent, $"No pude continuar: {ex.Message}", DateTimeOffset.Now));
        }
        finally
        {
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task DiscardPausedAsync(CancellationToken ct)
    {
        if (_autonomySessionId is null)
        {
            IsAgentPaused = false;
            return;
        }

        try
        {
            await _coordinator.CancelAsync(_autonomySessionId.Value, ct).ConfigureAwait(true);
        }
        catch (Exception)
        {
        }

        IsAgentPaused = false;
    }

    private static string PrepareTempWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "CA-A-IA-autonomy");
        var dir = Path.Combine(root, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PruneOldTempDirs()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "CA-A-IA-autonomy");
            if (!Directory.Exists(root))
            {
                return;
            }

            var cutoff = DateTimeOffset.UtcNow - PruneTempAfter;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(dir) < cutoff.UtcDateTime)
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private void AddMessage(ChatMessage message, bool persist = true)
    {
        if (_dispatcher.HasThreadAccess)
        {
            AppendMessage(message, persist);
        }
        else
        {
            _dispatcher.TryEnqueue(() => AppendMessage(message, persist));
        }
    }

    private void AppendMessage(ChatMessage message, bool persist = true)
    {
        if (persist)
        {
            _ = PersistMessageAsync(message, _autonomySessionId?.ToString("N"));
        }

        Messages.Add(message);
        while (Messages.Count > MaxMessages)
        {
            Messages.RemoveAt(0);
        }
    }

    private async Task PersistMessageAsync(ChatMessage message, string? sessionId)
    {
        try
        {
            await _history.AppendAsync(sessionId, ToStoredRole(message.Role), message.Text, message.At,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
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

    private void SetActivity(string? text, bool working = true)
    {
        if (text is null)
        {
            return;
        }

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

    /// <summary>Narración del agente: actividad en vivo + burbujas por tarea/herramienta.</summary>
    private Task OnAgentEventAsync(Domain.Events.AgentEvent e, CancellationToken ct)
    {
        // Solo MI sesión: el bus es compartido con el Chat y sin filtro cada
        // pestaña narraría (y persistiría duplicado) el trabajo de la otra.
        if (_autonomySessionId is null || e.Correlation?.SessionId != _autonomySessionId)
        {
            return Task.CompletedTask;
        }

        if (e.Type == AgentEventType.ToolStarted)
        {
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

        if (e.Type == AgentEventType.StateChanged)
        {
            var summary = e.Summary ?? string.Empty;
            if (summary.Contains("Completed") || summary.Contains("Failed")
                || summary.Contains("Cancelled"))
            {
                SetPaused(false);
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
                ClearAnd("Listo: he terminado. Revisa tu PC."),
            AgentEventType.StateChanged when (e.Summary ?? string.Empty).Contains("Failed") =>
                ClearAnd("No pude terminarlo. Mira la pestaña Salida para el detalle."),
            AgentEventType.StateChanged when (e.Summary ?? string.Empty).Contains("Cancelled") =>
                ClearAnd("Detenido. Pídeme otra cosa cuando quieras."),
            AgentEventType.StateChanged when (e.Summary ?? string.Empty).Contains("Paused") =>
                ClearAnd("En pausa. Pulsa Reanudar o escribe otra instrucción y Envía."),
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
