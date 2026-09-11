// CA-A-IA — Actualización automática desde GitHub Releases (contratos, sin dependencias).

namespace CaAIA.Domain.Update;

/// <summary>Versión disponible en el canal de releases.</summary>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string Notes,
    string DownloadUrl,
    long SizeBytes,
    string? Sha256);

/// <summary>Resultado de comprobar actualizaciones.</summary>
public sealed record UpdateCheckResult(
    Version CurrentVersion,
    UpdateInfo? AvailableUpdate)
{
    public bool HasUpdate => AvailableUpdate is not null;
}

/// <summary>
/// Actualizador de la app: comprueba el último release, descarga el instalador
/// verificándolo y lo lanza en silencioso contra la carpeta actual.
/// Implementación en Infrastructure (GitHub Releases).
/// </summary>
public interface IAppUpdateService
{
    Version CurrentVersion { get; }
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken);

    /// <summary>Descarga el instalador a una carpeta temporal. Informa 0..1.</summary>
    Task<string> DownloadAsync(
        UpdateInfo update, IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Lanza el instalador en silencioso (modo actualización: reabre la app al
    /// terminar) y devuelve false si no debe seguir corriendo esta instancia.
    /// </summary>
    bool LaunchInstaller(string installerPath, string installDirectory);
}
