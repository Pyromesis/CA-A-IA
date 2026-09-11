// CA-A-IA · Fase 0 — Composition Root: Host + configuración + DI + arranque WinUI.
// La View no contiene lógica de negocio: solo resuelve MainWindow del contenedor.

using CaAIA.Agent;
using CaAIA.Application;
using CaAIA.Infrastructure;
using CaAIA.Infrastructure.Logging;
using CaAIA.Presentation.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;

namespace CaAIA.Presentation;

public partial class App : Microsoft.UI.Xaml.Application
{
    private IHost? _host;
    private Window? _window;
    private Infrastructure.Process.GlobalHotkeys? _hotkeys;

    /// <summary>Contenedor raíz (Composition Root). Las Views lo usan para resolver páginas/VMs.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.SetBasePath(AppContext.BaseDirectory);
                    config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                    config.AddEnvironmentVariables(prefix: "CAAIA_");
#if DEBUG
                    config.AddUserSecrets(typeof(App).Assembly, optional: true);
#endif
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddCaAIALogging(context.Configuration);
                    services.AddApplication(context.Configuration);
                    services.AddInfrastructure(context.Configuration);
                    services.AddAgent();
                    services.AddPresentation();
                })
                .Build();

            Services = _host.Services;
            try
            {
                // Las tareas de arranque (p. ej. migración legacy) no deben impedir
                // que la app abra: degradar con log y continuar.
                await InfrastructureServiceExtensions
                    .RunStartupTasksAsync(Services, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Startup tasks failed: {ex}");
            }

            Services.GetRequiredService<Diagnostics.UiFlightRecorder>().Start(); // TEMPORARY-DIAGNOSTIC
            _window = Services.GetRequiredService<MainWindow>();
            _window.Closed += OnWindowClosed;
            _window.Activate();

            // Atajos globales: Mayús izq.+A pausa y Mayús izq.+S reanuda estén
            // donde estén (funcionan hasta con otra app al frente). Si el sistema
            // los deniega, la app sigue igual: los botones del Chat hacen lo mismo.
            try
            {
                _hotkeys = new Infrastructure.Process.GlobalHotkeys();
                if (!_hotkeys.Start(PauseFromHotkeyAsync, ResumeFromHotkeyAsync))
                {
                    _hotkeys.Dispose();
                    _hotkeys = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Hotkeys unavailable: {ex.GetType().Name}");
                _hotkeys = null;
            }

            // Actualizaciones: conecta el canal y busca en segundo plano (una vez).
            // Si hay release nuevo, Ajustes lo mostrará para descargar e instalar.
            try
            {
                var settings = Services.GetRequiredService<ViewModels.SettingsViewModel>();
                settings.AttachUpdater(
                    Services.GetRequiredService<Domain.Update.IAppUpdateService>(),
                    Services.GetRequiredService<Func<MainWindow>>());
                _ = settings.AutoCheckForUpdatesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Update wiring failed: {ex.GetType().Name}");
            }
        }
        catch (Exception ex)
        {
            // Fallo fatal antes de tener ventana: no hay UI donde mostrarlo; dejar
            // rastro y relanzar para que el informe de errores lo capture.
            System.Diagnostics.Debug.Fail($"Fatal startup failure: {ex}");
            throw;
        }
    }

    /// <summary>
    /// Drena lo pendiente en el cierre: eventos del bus, ajustes (proveedor,
    /// carpeta, nivel…) e historial del chat. Antes todo se perdía siempre.
    /// </summary>
    private async void OnWindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        try
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
        }
        catch (Exception)
        {
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var ct = cts.Token;
            try
            {
                // Ajustes primero (rápido, en memoria + SQLite settings).
                if (Services.GetService(typeof(Application.Services.IUserPreferences))
                    is Application.Services.IUserPreferences prefs)
                {
                    await prefs.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
            }

            try
            {
                // Historial del chat en vuelo.
                if (Services.GetService(typeof(ViewModels.ChatViewModel))
                    is ViewModels.ChatViewModel chat)
                {
                    await chat.FlushHistoryAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
            }

            if (Services.GetService(typeof(Domain.Events.IEventBus)) is IAsyncDisposable bus)
            {
                try
                {
                    await bus.DisposeAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Shutdown flush failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Ctrl+J global: pausa lo que esté corriendo (Chat y Autonomía).</summary>
    private static async Task PauseFromHotkeyAsync()
    {
        try
        {
            var chat = Services.GetRequiredService<ViewModels.ChatViewModel>();
            if (chat.PauseCommand.CanExecute(null))
            {
                await chat.PauseCommand.ExecuteAsync(null).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Hotkey pause failed: {ex.GetType().Name}");
        }

        try
        {
            var autonomy = Services.GetRequiredService<ViewModels.AutonomyViewModel>();
            if (autonomy.PauseCommand.CanExecute(null))
            {
                await autonomy.PauseCommand.ExecuteAsync(null).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Hotkey pause (autonomy) failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Ctrl+K global: reanuda donde se pausó.</summary>
    private static async Task ResumeFromHotkeyAsync()
    {
        try
        {
            var chat = Services.GetRequiredService<ViewModels.ChatViewModel>();
            if (chat.ResumeCommand.CanExecute(null))
            {
                await chat.ResumeCommand.ExecuteAsync(null).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Hotkey resume failed: {ex.GetType().Name}");
        }

        try
        {
            var autonomy = Services.GetRequiredService<ViewModels.AutonomyViewModel>();
            if (autonomy.ResumeCommand.CanExecute(null))
            {
                await autonomy.ResumeCommand.ExecuteAsync(null).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Hotkey resume (autonomy) failed: {ex.GetType().Name}");
        }
    }
}
