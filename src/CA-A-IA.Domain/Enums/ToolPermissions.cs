namespace CaAIA.Domain.Enums;

/// <summary>
/// Permisos de herramienta. Cada <see cref="Tools.ITool"/> declara los que requiere y cada
/// ejecución se autoriza contra la política vigente + scope (rutas permitidas/denegadas).
/// </summary>
[Flags]
public enum ToolPermission
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Delete = 1 << 2,
    Execute = 1 << 3,
    Network = 1 << 4,
    Git = 1 << 5,
    PackageInstall = 1 << 6,
    ProcessControl = 1 << 7,

    /// <summary>Lectura + información no destructiva (listados, diffs, status).</summary>
    ReadOnly = Read,
}

/// <summary>Categoría funcional de una herramienta (catálogo futuro, §14 del prompt).</summary>
public enum ToolKind
{
    Unknown = 0,
    FileSystem = 1,
    Search = 2,
    Command = 3,
    Build = 4,
    Test = 5,
    Git = 6,
    Process = 7,
    Documentation = 8,
    /// <summary>Ratón y teclado reales (pestaña Autonomía).</summary>
    UiAutomation = 9,
}
