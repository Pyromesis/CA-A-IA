// CA-A-IA — Herramientas de escritura (WriteFile, EditFile) y ejecución (ExecuteCommand).
// Toda invocación debe autorizarse ANTES vía IToolPermissionService + ExecutionScope
// (lo hace LlmTaskExecutor); estas clases ejecutan, no autorizan.

using System.Diagnostics;
using System.Text.Json;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Process;
using CaAIA.Domain.Tools;

namespace CaAIA.Infrastructure.Tools;

/// <summary>Crea o sobrescribe un fichero de texto. Argumentos: { "path", "content" }.</summary>
public sealed class WriteFileTool : ITool
{
    public const string ToolId = "WriteFile";
    private const int HardMaxChars = 500_000;

    public ToolDefinition Definition { get; } = new(
        ToolId, "WriteFile", "Creates or overwrites a text file. Parent directory must exist.",
        ToolKind.FileSystem, ToolPermission.Write,
        new[]
        {
            new ToolParameter("path", "Absolute file path.", "string", IsRequired: true),
            new ToolParameter("content", "Full text content.", "string", IsRequired: true),
        },
        TimeSpan.FromSeconds(30));

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var path = Required(doc, "path");
            var content = Required(doc, "content");
            if (path is null || content is null)
            {
                return Fail(invocation, "Missing required arguments 'path'/'content'.", sw);
            }

            if (content.Length > HardMaxChars)
            {
                return Fail(invocation, $"Content exceeds {HardMaxChars} chars.", sw);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(invocation.TimeoutOverride ?? Definition.DefaultTimeout);
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return Fail(invocation, $"Parent directory does not exist: {dir}.", sw);
            }

            await File.WriteAllTextAsync(path, content, timeoutCts.Token).ConfigureAwait(false);
            return Ok(invocation, $"Wrote {content.Length} chars to {path}.", sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(invocation, "WriteFile timed out.", sw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Fail(invocation, ex.Message, sw);
        }
    }

    internal static string? Required(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    internal static ToolResult Ok(ToolInvocation i, string output, Stopwatch sw) =>
        new(i.InvocationId, i.ToolId, true, output, Duration: sw.Elapsed);

    internal static ToolResult Fail(ToolInvocation i, string error, Stopwatch sw) =>
        new(i.InvocationId, i.ToolId, false, string.Empty,
            FailureCategory: FailureCategory.ToolFailure, Error: error, Duration: sw.Elapsed);
}

/// <summary>
/// Reemplazo exacto de un bloque de texto (1 ocurrencia). Argumentos: { "path", "oldText", "newText" }.
/// Falla si oldText aparece 0 o 2+ veces: nada de reemplazos ambiguos.
/// </summary>
public sealed class EditFileTool : ITool
{
    public const string ToolId = "EditFile";
    private const long HardMaxFileBytes = 1_000_000;

    public ToolDefinition Definition { get; } = new(
        ToolId, "EditFile", "Replaces exactly one occurrence of oldText with newText in a file.",
        ToolKind.FileSystem, ToolPermission.Write,
        new[]
        {
            new ToolParameter("path", "Absolute file path.", "string", IsRequired: true),
            new ToolParameter("oldText", "Exact text to replace (must occur once).", "string", IsRequired: true),
            new ToolParameter("newText", "Replacement text.", "string", IsRequired: true),
        },
        TimeSpan.FromSeconds(30));

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var path = WriteFileTool.Required(doc, "path");
            var oldText = WriteFileTool.Required(doc, "oldText");
            var newText = WriteFileTool.Required(doc, "newText");
            if (path is null || oldText is null || newText is null)
            {
                return WriteFileTool.Fail(invocation, "Missing required arguments 'path'/'oldText'/'newText'.", sw);
            }

            if (newText.Length > 500_000)
            {
                return WriteFileTool.Fail(invocation, "Replacement text exceeds 500000 chars.", sw);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(invocation.TimeoutOverride ?? Definition.DefaultTimeout);
            if (new FileInfo(path).Length > HardMaxFileBytes)
            {
                return WriteFileTool.Fail(invocation, $"File exceeds {HardMaxFileBytes} bytes; refusing edit.", sw);
            }

            var current = await File.ReadAllTextAsync(path, timeoutCts.Token).ConfigureAwait(false);

            var first = current.IndexOf(oldText, StringComparison.Ordinal);
            if (first < 0)
            {
                return WriteFileTool.Fail(invocation, "oldText not found in file.", sw);
            }

            if (current.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
            {
                return WriteFileTool.Fail(invocation, "oldText occurs more than once; refusing ambiguous edit.", sw);
            }

            var updated = current[..first] + newText + current[(first + oldText.Length)..];
            await File.WriteAllTextAsync(path, updated, timeoutCts.Token).ConfigureAwait(false);
            return WriteFileTool.Ok(invocation, $"Edited {path} ({oldText.Length} -> {newText.Length} chars).", sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WriteFileTool.Fail(invocation, "EditFile timed out.", sw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return WriteFileTool.Fail(invocation, ex.Message, sw);
        }
    }
}

/// <summary>
/// Ejecuta un comando en un directorio de trabajo. Argumentos: { "command", "args": [], "workdir", "timeoutSeconds"? }.
/// El workdir debe estar dentro del ExecutionScope (lo valida el ejecutor). Sin shell: sin expansión.
/// Salida truncada a 20k chars. Exit != 0 → fallo con la salida como evidencia.
/// </summary>
public sealed class ExecuteCommandTool : ITool
{
    public const string ToolId = "ExecuteCommand";
    private const int MaxOutputChars = 20_000;

    private readonly ICommandRunner _runner;

    public ExecuteCommandTool(ICommandRunner runner)
    {
        _runner = runner;
    }

    public ToolDefinition Definition { get; } = new(
        ToolId, "ExecuteCommand", "Runs a command in a working directory and returns its output.",
        ToolKind.Command, ToolPermission.Execute,
        new[]
        {
            new ToolParameter("command", "Executable (no shell expansion).", "string", IsRequired: true),
            new ToolParameter("args", "Arguments array.", "array", IsRequired: false, DefaultJson: "[]"),
            new ToolParameter("workdir", "Working directory (must be inside the execution scope).", "string", IsRequired: true),
            new ToolParameter("timeoutSeconds", "Timeout.", "integer", IsRequired: false, DefaultJson: "120"),
        },
        TimeSpan.FromSeconds(120));

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var command = WriteFileTool.Required(doc, "command");
            if (string.IsNullOrWhiteSpace(command))
            {
                return WriteFileTool.Fail(invocation, "Missing required argument 'command'.", sw);
            }

            var args = new List<string>();
            if (doc.RootElement.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in a.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        args.Add(item.GetString()!);
                    }
                }
            }

            var timeoutSeconds = doc.RootElement.TryGetProperty("timeoutSeconds", out var t) && t.TryGetInt32(out var n)
                ? Math.Clamp(n, 5, 300) : 120;
            // El ejecutor impone su propio techo (TimeoutOverride): el LLM no puede extenderlo.
            if (invocation.TimeoutOverride.HasValue)
            {
                timeoutSeconds = (int)Math.Min(timeoutSeconds,
                    Math.Clamp(invocation.TimeoutOverride.Value.TotalSeconds, 5, 300));
            }
            var workdir = WriteFileTool.Required(doc, "workdir");
            if (string.IsNullOrWhiteSpace(workdir) || !Directory.Exists(workdir))
            {
                return WriteFileTool.Fail(invocation, "Missing or invalid 'workdir' (must exist).", sw);
            }

            CommandResult result;
            try
            {
                result = await _runner.RunAsync(command, args, workdir,
                    TimeSpan.FromSeconds(timeoutSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                return WriteFileTool.Fail(invocation, ex.Message, sw);
            }

            var output = $"exit={result.ExitCode} in {result.Duration.TotalSeconds:F1}s\n{result.StandardOutput}{result.StandardError}";
            if (output.Length > MaxOutputChars)
            {
                output = output[..MaxOutputChars] + "\n…[truncated]";
            }

            return result.Success
                ? WriteFileTool.Ok(invocation, output, sw)
                : new ToolResult(invocation.InvocationId, invocation.ToolId, false, output,
                    FailureCategory: FailureCategory.ToolFailure,
                    Error: $"Command exited with code {result.ExitCode}.", Duration: sw.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return WriteFileTool.Fail(invocation, ex.Message, sw);
        }
    }
}
