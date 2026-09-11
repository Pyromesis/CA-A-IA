namespace CaAIA.Domain.Enums;

/// <summary>
/// Clasificación de fallos. Distinguir el origen evita que el agente modifique código
/// cuando el problema real es el entorno, el proveedor o los permisos (Fase 0, §20).
/// </summary>
public enum FailureCategory
{
    Unknown = 0,

    /// <summary>El código generado es incorrecto; reparar modificando código.</summary>
    CodeError = 1,

    /// <summary>Una prueba falla (el build funciona).</summary>
    TestFailure = 2,

    /// <summary>El proyecto no compila.</summary>
    BuildFailure = 3,

    /// <summary>Dependencia externa caída o incompatible. NO tocar código de negocio.</summary>
    DependencyFailure = 4,

    /// <summary>Toolchain/SDK/SO. NO tocar código de negocio.</summary>
    EnvironmentFailure = 5,

    /// <summary>Permiso denegado (fichero, proceso, scope). Escalar, no reintentar a ciegas.</summary>
    PermissionFailure = 6,

    /// <summary>Fallo de red. Reintentar con backoff.</summary>
    NetworkFailure = 7,

    /// <summary>El proveedor de IA falló (rate limit, modelo, auth). Rotar/reintentar.</summary>
    ProviderFailure = 8,

    /// <summary>Una herramienta falló (timeout, crash, contrato).</summary>
    ToolFailure = 9,
}
