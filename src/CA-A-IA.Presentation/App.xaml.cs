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
}
