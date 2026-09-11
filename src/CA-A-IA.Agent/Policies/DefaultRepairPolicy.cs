// CA-A-IA · Fase 0 — Política de reparación por categoría de fallo (§19, §20).

using CaAIA.Domain.Enums;
using CaAIA.Domain.Execution;

namespace CaAIA.Agent.Policies;

/// <summary>
/// Regla: se repara lo propio del código (Code/Test/Build/Tool) dentro del límite de intentos;
/// red/proveedor con backoff; entorno/dependencias/permisos/desconocido escalan (NO se reintenta
/// a ciegas para no destrozar código por un problema externo).
/// </summary>
public sealed class DefaultRepairPolicy : IRepairPolicy
{
    public bool ShouldRepair(FailureCategory category, int attempts, int maxAttempts)
    {
        if (attempts >= maxAttempts)
        {
            return false;
        }

        return category switch
        {
            FailureCategory.CodeError => true,
            FailureCategory.TestFailure => true,
            FailureCategory.BuildFailure => true,
            FailureCategory.ToolFailure => true,
            FailureCategory.NetworkFailure => true,
            FailureCategory.ProviderFailure => true,
            FailureCategory.DependencyFailure => false,
            FailureCategory.EnvironmentFailure => false,
            FailureCategory.PermissionFailure => false,
            _ => false,
        };
    }

    public TimeSpan DelayBeforeRetry(FailureCategory category, int attempts)
    {
        var backoffMs = Math.Min(30_000, 1000 * Math.Pow(2, Math.Max(0, attempts - 1)));
        // Jitter: evita que N tareas fallidas a la vez reintenten en estampida.
        backoffMs += Random.Shared.Next(0, 500);
        return category switch
        {
            FailureCategory.NetworkFailure => TimeSpan.FromMilliseconds(backoffMs),
            FailureCategory.ProviderFailure => TimeSpan.FromMilliseconds(backoffMs),
            FailureCategory.BuildFailure => TimeSpan.FromSeconds(2),
            _ => TimeSpan.FromSeconds(1),
        };
    }
}
