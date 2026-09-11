using CaAIA.Domain.Enums;

namespace CaAIA.Domain.Security;

/// <summary>
/// Scope de ejecución que acota lo que el agente puede tocar (§15, §24).
/// El agente es NO confiable por diseño: todo acceso se valida contra este scope.
/// </summary>
public sealed record ExecutionScope(
    IReadOnlyList<string> AllowedPaths,
    IReadOnlyList<string> DeniedPaths,
    ToolPermission GrantedPermissions,
    bool RequireConfirmationForWrite,
    bool RequireConfirmationForExecute)
{
    public static ExecutionScope ReadOnlyWorkspace(string workspacePath) => new(
        AllowedPaths: new[] { workspacePath },
        DeniedPaths: Array.Empty<string>(),
        GrantedPermissions: ToolPermission.Read,
        RequireConfirmationForWrite: true,
        RequireConfirmationForExecute: true);

    /// <summary>
    /// Scope según el nivel de autorización del usuario. Las rutas (permitidas/denegadas)
    /// se respetan en los 3 niveles: el nivel solo cambia QUÉ permisos y SI se confirma.
    /// </summary>
    public static ExecutionScope FromAuthorizationLevel(
        string workspacePath,
        IReadOnlyList<string> deniedPaths,
        AuthorizationLevel level,
        bool allowPackageInstall) => level switch
    {
        // Nivel 2: edita ficheros sin pedir; sin Execute/ProcessControl/PackageInstall.
        AuthorizationLevel.EditWithoutPcControl => new ExecutionScope(
            AllowedPaths: new[] { workspacePath },
            DeniedPaths: deniedPaths,
            GrantedPermissions: ToolPermission.Read | ToolPermission.Write,
            RequireConfirmationForWrite: false,
            RequireConfirmationForExecute: true),

        // Nivel 3: control total del workspace sin pedir (incluye ejecutar comandos).
        AuthorizationLevel.FullControl => new ExecutionScope(
            AllowedPaths: new[] { workspacePath },
            DeniedPaths: deniedPaths,
            GrantedPermissions: ToolPermission.Read | ToolPermission.Write | ToolPermission.Delete
                | ToolPermission.Execute | ToolPermission.Network | ToolPermission.Git
                | ToolPermission.ProcessControl
                | (allowPackageInstall ? ToolPermission.PackageInstall : ToolPermission.None),
            RequireConfirmationForWrite: false,
            RequireConfirmationForExecute: false),

        // Nivel 1 (y desconocido → lo más seguro): todo cambio con confirmación,
        // incluido el control del PC (ratón/teclado de Autonomía: cada acción pregunta).
        _ => new ExecutionScope(
            AllowedPaths: new[] { workspacePath },
            DeniedPaths: deniedPaths,
            GrantedPermissions: ToolPermission.Read | ToolPermission.Write | ToolPermission.Execute
                | ToolPermission.ProcessControl
                | (allowPackageInstall ? ToolPermission.PackageInstall : ToolPermission.None),
            RequireConfirmationForWrite: true,
            RequireConfirmationForExecute: true),
    };

    public bool IsPathAllowed(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return false;
        }

        // Rechazar rutas UNC/extended-length: fuera del modelo de workspace local.
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var denied in DeniedPaths)
        {
            if (IsWithinOrEqual(full, denied))
            {
                return false;
            }
        }

        return AllowedPaths.Any(a => IsWithinOrEqual(full, a));
    }

    private static bool IsWithinOrEqual(string candidateFullPath, string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return false;
        }

        string baseFull;
        try
        {
            baseFull = Path.GetFullPath(basePath);
        }
        catch (Exception)
        {
            return false;
        }

        if (string.Equals(candidateFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            baseFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = baseFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidateFullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Nivel de autorización del agente (lo elige el usuario en Ajustes).
/// Nivel 1: el usuario aprueba cada cambio. Nivel 2: la IA edita ficheros sin pedir
/// pero NO puede controlar el PC (sin ExecuteCommand). Nivel 3: edita y controla
/// el PC sin pedir autorización.
/// </summary>
public enum AuthorizationLevel
{
    /// <summary>Nivel 1: escritura y ejecución siempre con confirmación del usuario.</summary>
    ConfirmChanges = 1,

    /// <summary>Nivel 2: escritura sin confirmación; ejecución denegada (sin control del PC).</summary>
    EditWithoutPcControl = 2,

    /// <summary>Nivel 3: escritura y ejecución sin confirmación (control total del workspace).</summary>
    FullControl = 3,
}

/// <summary>Decisión de autorización de una invocación de herramienta.</summary>
public sealed record PermissionDecision(bool Allowed, string Reason, bool RequiresUserConfirmation);

/// <summary>Servicio de permisos de herramientas (§15). Implementación en Infrastructure.</summary>
public interface IToolPermissionService
{
    PermissionDecision Authorize(Tools.ToolInvocation invocation, Tools.ToolDefinition definition, ExecutionScope scope);
}

/// <summary>
/// Almacén seguro de secretos (API keys). En Windows: Credential Manager / DPAPI
/// (implementación en Infrastructure). NUNCA en appsettings ni logs.
/// </summary>
public interface ISecretStore
{
    Task StoreAsync(string key, string secret, CancellationToken cancellationToken);
    Task<string?> RetrieveAsync(string key, CancellationToken cancellationToken);
    Task RemoveAsync(string key, CancellationToken cancellationToken);
}
