// CA-A-IA — Panel de ajustes: API keys en el almacén seguro + prueba de conexión.
//
// NOTA (MVVMTK0045): ver ViewModels.cs (campos con [ObservableProperty], sin AOT/trimming).
#pragma warning disable MVVMTK0045

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaAIA.Presentation.ViewModels;

/// <summary>API keys (Credential Manager) y prueba real con modelo gratis.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private const string ZenSecret = "opencode-zen-api-key";
    private const string RouterSecret = "openrouter-api-key";

    private readonly Domain.Security.ISecretStore _secrets;
    private readonly Domain.AI.IProviderRegistry _providers;

    [ObservableProperty]
    private string _zenKey = string.Empty;

    [ObservableProperty]
    private string _routerKey = string.Empty;

    [ObservableProperty]
    private string _zenStatus = "Sin comprobar.";

    [ObservableProperty]
    private string _routerStatus = "Sin comprobar.";

    [ObservableProperty]
    private string _localStatus = "Sin comprobar.";

    [ObservableProperty]
    private string _opencodeStatus = "Sin comprobar.";

    [ObservableProperty]
    private bool _isBusy;

    // ---- Actualizaciones automáticas (GitHub Releases, firma CA) ----

    [ObservableProperty]
    private string _appVersionText =
        $"Versión instalada: {AppVersion()}";

    [ObservableProperty]
    private string _updateStatus = "Aún no se ha buscado actualizaciones.";

    [ObservableProperty]
    private double _updateProgress;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _availableVersionText = string.Empty;

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    private Domain.Update.IAppUpdateService? _updater;
    private Func<MainWindow>? _window;
    private Domain.Update.UpdateInfo? _pendingUpdate;
    private bool _autoChecked;

    private static string AppVersion()
    {
        var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        return v is null ? "?" : $"{Math.Max(0, v.Major)}.{Math.Max(0, v.Minor)}.{Math.Max(0, v.Build)}";
    }

    public SettingsViewModel(
        Domain.Security.ISecretStore secrets,
        Domain.AI.IProviderRegistry providers)
    {
        _secrets = secrets;
        _providers = providers;
        _ = RefreshStatusesAsync();
    }

    /// <summary>Conecta el actualizador (separado del ctor para no romper DI/tests).</summary>
    public void AttachUpdater(Domain.Update.IAppUpdateService updater, Func<MainWindow> window)
    {
        _updater = updater;
        _window = window;
    }

    /// <summary>Chequeo silencioso al arrancar (una vez): solo avisa si hay algo nuevo.</summary>
    public async Task AutoCheckForUpdatesAsync()
    {
        if (_autoChecked || _updater is null)
        {
            return;
        }

        _autoChecked = true;
        await CheckUpdatesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync(CancellationToken ct)
    {
        if (_updater is null)
        {
            UpdateStatus = "Actualizador no disponible en esta compilación.";
            return;
        }

        if (IsCheckingUpdate || IsDownloadingUpdate)
        {
            return;
        }

        IsCheckingUpdate = true;
        try
        {
            UpdateStatus = "Buscando actualizaciones…";
            var result = await _updater.CheckForUpdatesAsync(ct).ConfigureAwait(true);
            _pendingUpdate = result.AvailableUpdate;
            if (_pendingUpdate is null)
            {
                UpdateAvailable = false;
                AvailableVersionText = string.Empty;
                UpdateStatus = $"Estás al día (versión {AppVersion()}).";
            }
            else
            {
                UpdateAvailable = true;
                AvailableVersionText = _pendingUpdate.Version.ToString();
                UpdateStatus = $"Disponible la versión {_pendingUpdate.Version} " +
                    $"({_pendingUpdate.SizeBytes / 1_000_000} MB). Pulsa Descargar e instalar.";
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus = "Búsqueda cancelada.";
        }
        catch (Exception ex)
        {
            UpdateAvailable = false;
            UpdateStatus = $"No se pudo buscar: {ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallAsync(CancellationToken ct)
    {
        if (_updater is null || _window is null)
        {
            UpdateStatus = "Actualizador no disponible en esta compilación.";
            return;
        }

        if (IsDownloadingUpdate || IsCheckingUpdate)
        {
            return;
        }

        if (_pendingUpdate is null)
        {
            UpdateStatus = "Primero busca actualizaciones.";
            return;
        }

        // Solo tiene sentido actualizar una copia instalada (con desinstalador):
        // en desarrollo (bin/) instalar encima rompería el entorno.
        var installDir = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(installDir, "unins000.exe")))
        {
            UpdateStatus = "Esta copia no se instaló con el instalador (modo desarrollo): " +
                "descarga el setup de Lanzamientos en GitHub.";
            return;
        }

        IsDownloadingUpdate = true;
        UpdateProgress = 0;
        try
        {
            UpdateStatus = $"Descargando {_pendingUpdate.Version}… (no cierres la app)";
            // Progress<T> captura el contexto UI: el % llega al hilo correcto.
            var progress = new Progress<double>(p => { UpdateProgress = p * 100; });
            var installer = await _updater.DownloadAsync(_pendingUpdate, progress, ct)
                .ConfigureAwait(true);
            UpdateStatus = "Descarga verificada. Instalando… la app se cerrará y reabrirá sola.";
            if (!_updater.LaunchInstaller(installer, installDir))
            {
                UpdateStatus = "No se pudo lanzar el instalador.";
                return;
            }

            _window().Close(); // el instalador (/CLOSEAPPLICATIONS + /UPDATE) hace el resto
        }
        catch (OperationCanceledException)
        {
            UpdateStatus = "Descarga cancelada.";
        }
        catch (Exception ex)
        {
            UpdateStatus = $"No se pudo actualizar: {ex.Message}";
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    private async Task RefreshStatusesAsync()
    {
        try
        {
            var zen = await _secrets.RetrieveAsync(ZenSecret, CancellationToken.None).ConfigureAwait(true);
            ZenStatus = string.IsNullOrEmpty(zen) ? "Sin configurar." : "Guardada.";
            var router = await _secrets.RetrieveAsync(RouterSecret, CancellationToken.None).ConfigureAwait(true);
            RouterStatus = string.IsNullOrEmpty(router) ? "Sin configurar." : "Guardada.";
        }
        catch (Exception)
        {
        }
    }

    [RelayCommand]
    private async Task SaveZenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ZenKey))
        {
            ZenStatus = "Pega primero la key.";
            return;
        }

        await _secrets.StoreAsync(ZenSecret, ZenKey.Trim(), ct).ConfigureAwait(true);
        ZenKey = string.Empty;
        ZenStatus = "Guardada en el almacén seguro.";
    }

    [RelayCommand]
    private async Task SaveRouterAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(RouterKey))
        {
            RouterStatus = "Pega primero la key.";
            return;
        }

        await _secrets.StoreAsync(RouterSecret, RouterKey.Trim(), ct).ConfigureAwait(true);
        RouterKey = string.Empty;
        RouterStatus = "Guardada en el almacén seguro.";
    }

    [RelayCommand]
    private async Task TestZenAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            ZenStatus = "Probando con big-pickle (gratis)…";
            var provider = _providers.Get("opencode-zen");
            var response = await provider.CompleteAsync(new Domain.AI.AIRequest
            {
                ModelId = "big-pickle",
                Messages = [new Domain.AI.AIMessage(Domain.AI.AIRole.User, "Di OK")],
                Timeout = TimeSpan.FromSeconds(60),
                Correlation = Domain.Correlation.CorrelationContext.Create(Guid.Empty, Guid.Empty),
            }, ct).ConfigureAwait(true);
            ZenStatus = $"OK: {response.Content.Trim()}";
        }
        catch (Exception ex)
        {
            ZenStatus = $"Fallo: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestLocalAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            LocalStatus = "Buscando Ollama o LM Studio en local…";
            Domain.AI.IAIProvider provider;
            try
            {
                provider = _providers.Get("local");
            }
            catch (KeyNotFoundException)
            {
                LocalStatus = "Proveedor local no registrado.";
                return;
            }

            var models = await provider.GetModelsAsync(ct).ConfigureAwait(true);
            LocalStatus = models.Count == 0
                ? "No detectado. Instala Ollama y haz 'ollama pull qwen3:8b' (o arranca el servidor de LM Studio), luego pulsa Detectar."
                : $"Detectado: {models.Count} modelos ({string.Join(", ", models.Take(3).Select(m => m.Id))}).";
        }
        catch (Exception ex)
        {
            LocalStatus = $"Fallo: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
    [RelayCommand]
    private async Task TestOpencodeAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            OpencodeStatus = "Buscando CLI de OpenCode…";
            Domain.AI.IAIProvider provider;
            try
            {
                provider = _providers.Get("opencode");
            }
            catch (KeyNotFoundException)
            {
                OpencodeStatus = "Proveedor opencode no registrado.";
                return;
            }

            if (!await provider.CheckHealthAsync(ct).ConfigureAwait(true))
            {
                OpencodeStatus = "No instalado o sin responder. Instala con 'npm install -g opencode-ai' " +
                    "y haz 'opencode auth login'; el tier gratis funciona a través de OpenCode logueado.";
                return;
            }

            OpencodeStatus = "OpenCode CLI listo. Tras 'opencode auth login', elige el proveedor " +
                "OpenCode y un modelo gratis en el Chat.";
        }
        catch (Exception ex)
        {
            OpencodeStatus = $"Fallo: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestRouterAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            RouterStatus = "Buscando modelo gratis…";
            var provider = _providers.Get("openrouter");
            var models = await provider.GetModelsAsync(ct).ConfigureAwait(true);
            var free = models.FirstOrDefault(m => m.IsFree);
            if (free is null)
            {
                RouterStatus = "Catálogo sin gratis visibles; la key se validará al usarla.";
                return;
            }

            var response = await provider.CompleteAsync(new Domain.AI.AIRequest
            {
                ModelId = free.Id,
                Messages = [new Domain.AI.AIMessage(Domain.AI.AIRole.User, "Di OK")],
                Timeout = TimeSpan.FromSeconds(60),
                Correlation = Domain.Correlation.CorrelationContext.Create(Guid.Empty, Guid.Empty),
            }, ct).ConfigureAwait(true);
            RouterStatus = $"OK con {free.Id}: {response.Content.Trim()}";
        }
        catch (Exception ex)
        {
            RouterStatus = $"Fallo: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
