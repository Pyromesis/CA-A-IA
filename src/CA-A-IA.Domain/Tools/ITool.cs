using CaAIA.Domain.Enums;

namespace CaAIA.Domain.Tools;

/// <summary>Parámetro declarado de una herramienta (esquema JSON Schema simplificado).</summary>
public sealed record ToolParameter(
    string Name,
    string Description,
    string JsonType,
    bool IsRequired,
    string? DefaultJson = null);

/// <summary>Definición pública de una herramienta (lo que ve el modelo).</summary>
public sealed record ToolDefinition(
    string Id,
    string Name,
    string Description,
    ToolKind Kind,
    ToolPermission RequiredPermissions,
    IReadOnlyList<ToolParameter> Parameters,
    TimeSpan DefaultTimeout);

/// <summary>Invocación concreta de una herramienta (auditable).</summary>
public sealed record ToolInvocation(
    Guid InvocationId,
    string ToolId,
    string ArgumentsJson,
    Correlation.CorrelationContext Correlation,
    TimeSpan? TimeoutOverride = null);

/// <summary>Resultado de ejecución de herramienta: éxito, denegación o fallo clasificado.</summary>
public sealed record ToolResult(
    Guid InvocationId,
    string ToolId,
    bool Success,
    string Output,
    bool PermissionDenied = false,
    FailureCategory? FailureCategory = null,
    string? Error = null,
    TimeSpan? Duration = null,
    string? AttachmentPath = null);

/// <summary>
/// Contrato de herramienta del agente (§14). Implementaciones en Infrastructure;
/// el agente solo programa contra esta interfaz + <see cref="IToolRegistry"/>.
/// </summary>
public interface ITool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>Catálogo de herramientas disponibles para el agente.</summary>
public interface IToolRegistry
{
    void Register(ITool tool);
    ITool Get(string toolId);
    IReadOnlyCollection<ToolDefinition> ListDefinitions();
    IReadOnlyCollection<ToolDefinition> ListDefinitionsFor(ToolPermission granted);
}
