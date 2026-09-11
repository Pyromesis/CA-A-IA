namespace CaAIA.Domain.AI;

/// <summary>Capacidades declaradas por un proveedor/modelo (§10). Permiten selección y degradación.</summary>
[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Streaming = 1 << 0,
    Tools = 1 << 1,
    Vision = 1 << 2,
    StructuredOutput = 1 << 3,
    Reasoning = 1 << 4,
    Embeddings = 1 << 5,
    LongContext = 1 << 6,
    CodeExecution = 1 << 7,
}

/// <summary>Metadatos de un modelo ofertado por un proveedor.</summary>
public sealed record AIModel(
    string Id,
    string DisplayName,
    string ProviderId,
    int? ContextWindowTokens = null,
    ProviderCapabilities Capabilities = ProviderCapabilities.None,
    bool IsFree = false)
{
    /// <summary>
    /// Niveles de esfuerzo aceptados en minúsculas (p. ej. "low", "xhigh").
    /// Vacío = se aceptan todos o se desconoce (no filtra).
    /// Fuentes: OpenRouter (`reasoning.supported_efforts`), models.dev (`reasoning_options`).
    /// </summary>
    public IReadOnlyList<string> SupportedEfforts { get; init; } = Array.Empty<string>();
}

/// <summary>Rol de un mensaje en una petición.</summary>
public enum AIRole
{
    System = 0,
    User = 1,
    Assistant = 2,
    Tool = 3,
}

/// <summary>Mensaje individual (incluye llamadas a herramientas del asistente e imágenes locales).</summary>
public sealed record AIMessage(
    AIRole Role,
    string Content,
    IReadOnlyList<AIToolCall> ToolCallsMaker = null!,
    string? ToolCallId = null,
    IReadOnlyList<string>? ImagePaths = null)
{
    public IReadOnlyList<AIToolCall> ToolCalls { get; init; } = ToolCallsMaker ?? Array.Empty<AIToolCall>();

    /// <summary>Rutas locales de imágenes adjuntas (capturas). Cada adaptador las convierte.</summary>
    public IReadOnlyList<string> Images { get; init; } = ImagePaths ?? Array.Empty<string>();
}

/// <summary>Solicitud de tool/function call emitida por el modelo.</summary>
public sealed record AIToolCall(string Id, string ToolName, string ArgumentsJson);

/// <summary>Resultado de herramienta devuelto al modelo.</summary>
public sealed record AIToolResult(string ToolCallId, string ToolName, string Content, bool IsError = false);

/// <summary>Uso de tokens de una completion (observabilidad, §26).</summary>
public sealed record TokenUsage(int PromptTokens, int CompletionTokens)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>Petición normalizada e independiente del proveedor.</summary>
public sealed class AIRequest
{
    public required string ModelId { get; init; }
    public IReadOnlyList<AIMessage> Messages { get; init; } = Array.Empty<AIMessage>();
    public IReadOnlyList<AIToolResult> ToolResults { get; init; } = Array.Empty<AIToolResult>();
    public IReadOnlyList<Tools.ToolDefinition> Tools { get; init; } = Array.Empty<Tools.ToolDefinition>();
    public double Temperature { get; init; } = 0.2;
    public int? MaxOutputTokens { get; init; }
    public string? StructuredOutputSchema { get; init; }
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Esfuerzo de razonamiento ("minimal", "low", "medium", "high", "xhigh").
    /// Nulo/vacío = default del proveedor. Solo lo envían los adaptadores que lo
    /// soportan de forma verificada (OpenCode: campo `variant` de su spec).
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Contexto de correlación para logging distribuido (§26).</summary>
    public required Correlation.CorrelationContext Correlation { get; init; }
}

/// <summary>Respuesta normalizada. El streaming se modela como <c>IAsyncEnumerable&lt;AIStreamChunk&gt;</c>.</summary>
public sealed record AIResponse(
    string Content,
    IReadOnlyList<AIToolCall> ToolCalls,
    string ModelId,
    TokenUsage Usage,
    string? FinishReason = null);

/// <summary>Fragmento de respuesta en streaming.</summary>
public sealed record AIStreamChunk(string Delta, AIToolCall? PartialToolCall = null, bool IsFinal = false);
