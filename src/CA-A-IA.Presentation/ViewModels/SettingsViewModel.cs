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

    public SettingsViewModel(
        Domain.Security.ISecretStore secrets,
        Domain.AI.IProviderRegistry providers)
    {
        _secrets = secrets;
        _providers = providers;
        _ = RefreshStatusesAsync();
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
