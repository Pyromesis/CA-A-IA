// CA-A-IA · Fase 0 — Configuración central tipada (§23).
// Bindeada desde appsettings.json + variables de entorno + user-secrets (ver Infrastructure).
// NUNCA contiene secretos: las API keys viven en ISecretStore (Credential Manager / DPAPI).

namespace CaAIA.Application.Configuration;

/// <summary>Raíz de configuración <c>"CaAIA"</c> en appsettings.json.</summary>
public sealed class CaAIAOptions
{
    public const string SectionName = "CaAIA";

    public AgentSettings Agent { get; init; } = new();
    public ProviderSettings Providers { get; init; } = new();
    public ExecutionSettings Execution { get; init; } = new();
    public SecuritySettings Security { get; init; } = new();
    public WorkspaceSettings Workspace { get; init; } = new();
    public PersistenceSettings Persistence { get; init; } = new();
}

public sealed class AgentSettings
{
    public int MaxRepairAttempts { get; init; } = 3;
    public bool RequirePlanApproval { get; init; } = true;
    public int MaxTasksPerPlan { get; init; } = 50;

    /// <summary>Iteraciones máximas del bucle herramienta→modelo por tarea (LlmTaskExecutor).</summary>
    public int MaxToolIterations { get; init; } = 25;

    /// <summary>Caracteres máximos de contexto de proyecto por tarea.</summary>
    public int MaxTaskContextChars { get; init; } = 40_000;
}

public sealed class ProviderSettings
{
    public string DefaultProviderId { get; init; } = "opencode";
    public string DefaultModelId { get; init; } = string.Empty;
    public int RequestTimeoutSeconds { get; init; } = 120;
    public int MaxRetries { get; init; } = 3;

    /// <summary>Nombres de proveedores habilitados (el resto se registra pero queda inactivo).</summary>
    public IReadOnlyList<string> EnabledProviders { get; init; } = new[] { "opencode", "openrouter" };
}

public sealed class ExecutionSettings
{
    public int OperationTimeoutSeconds { get; init; } = 600;
    public int GlobalTimeoutMinutes { get; init; } = 480;
    public int HeartbeatSeconds { get; init; } = 30;
    public int StallDetectionSeconds { get; init; } = 300;
    public int ToolTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Rondas máximas de auditoría final con ampliación. Sin convergencia tras
    /// N rondas, la ejecución falla honestamente en vez de alargar el plan
    /// eternamente (el juez, sobre todo el semántico, puede no converger nunca).
    /// </summary>
    public int MaxAuditRounds { get; init; } = 2;
}

public sealed class SecuritySettings
{
    public bool RequireConfirmationForWrite { get; init; } = true;
    public bool RequireConfirmationForExecute { get; init; } = true;
    public bool AllowPackageInstall { get; init; } = false;
    public IReadOnlyList<string> DeniedPaths { get; init; } = Array.Empty<string>();
}

public sealed class WorkspaceSettings
{
    public string DefaultWorkspacePath { get; init; } = string.Empty;
    public int MaxContextChars { get; init; } = 60_000;
}

public sealed class PersistenceSettings
{
    /// <summary>
    /// Ruta base de datos locales. Vacío = <c>%LocalAppData%/CA-A-IA</c>.
    /// SQLite (WAL) en fases posteriores; Fase 0 usa JSON transaccional (ver docs).
    /// </summary>
    public string DataPath { get; init; } = string.Empty;
}
