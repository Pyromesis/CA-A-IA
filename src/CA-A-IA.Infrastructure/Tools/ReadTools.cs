// CA-A-IA · Fase 0 — Herramientas de lectura reales (ReadFile, ListDirectory).
// Solo requieren permiso Read; respetan timeout + cancelación + límite de tamaño.

using System.Diagnostics;
using System.Text.Json;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Tools;

namespace CaAIA.Infrastructure.Tools;

/// <summary>Lee un fichero de texto con límite de tamaño. Argumentos: { "path": "...", "maxChars": 20000 }.</summary>
public sealed class ReadFileTool : ITool
{
    public const string ToolId = "ReadFile";
    private const int HardMaxChars = 200_000;

    public ToolDefinition Definition { get; } = new(
        ToolId, "ReadFile", "Reads a text file from the workspace.",
        ToolKind.FileSystem, ToolPermission.Read,
        new[]
        {
            new ToolParameter("path", "Absolute file path.", "string", IsRequired: true),
            new ToolParameter("maxChars", "Maximum characters to return.", "integer", IsRequired: false, DefaultJson: "20000"),
        },
        TimeSpan.FromSeconds(30));

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            if (!doc.RootElement.TryGetProperty("path", out var pathEl) ||
                string.IsNullOrWhiteSpace(pathEl.GetString()))
            {
                return Fail(invocation, "Missing required argument 'path'.", sw);
            }

            var path = pathEl.GetString()!;
            var maxChars = doc.RootElement.TryGetProperty("maxChars", out var m) && m.TryGetInt32(out var n)
                ? Math.Min(n, HardMaxChars) : 20_000;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(EffectiveTimeout(invocation));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            using var reader = new StreamReader(stream);
            var capacity = Math.Min(maxChars, HardMaxChars);
            char[] buffer = new char[capacity + 1]; // +1 para detectar truncado sin EndOfStream (CA2024)
            var read = await reader.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
            var truncated = read > capacity;
            var output = new string(buffer, 0, Math.Min(read, capacity)) + (truncated ? "\n…[truncated]" : string.Empty);
            return Ok(invocation, output, sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(invocation, "ReadFile timed out.", sw, FailureCategory.ToolFailure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Fail(invocation, ex.Message, sw, FailureCategory.ToolFailure);
        }
    }

    private TimeSpan EffectiveTimeout(ToolInvocation invocation) =>
        invocation.TimeoutOverride ?? Definition.DefaultTimeout;

    private static ToolResult Ok(ToolInvocation i, string output, Stopwatch sw) =>
        new(i.InvocationId, i.ToolId, true, output, Duration: sw.Elapsed);

    private static ToolResult Fail(ToolInvocation i, string error, Stopwatch sw, FailureCategory category = FailureCategory.ToolFailure) =>
        new(i.InvocationId, i.ToolId, false, string.Empty, FailureCategory: category, Error: error, Duration: sw.Elapsed);
}

/// <summary>Lista un directorio (no recursivo por defecto). Argumentos: { "path": "...", "pattern": "*" }.</summary>
public sealed class ListDirectoryTool : ITool
{
    public const string ToolId = "ListDirectory";

    public ToolDefinition Definition { get; } = new(
        ToolId, "ListDirectory", "Lists files and directories in a workspace folder.",
        ToolKind.FileSystem, ToolPermission.Read,
        new[]
        {
            new ToolParameter("path", "Absolute directory path.", "string", IsRequired: true),
            new ToolParameter("pattern", "Search pattern.", "string", IsRequired: false, DefaultJson: "\"*\""),
        },
        TimeSpan.FromSeconds(30));

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            if (!doc.RootElement.TryGetProperty("path", out var pathEl) ||
                string.IsNullOrWhiteSpace(pathEl.GetString()))
            {
                return Task.FromResult(new ToolResult(invocation.InvocationId, invocation.ToolId, false,
                    string.Empty, FailureCategory: FailureCategory.ToolFailure,
                    Error: "Missing required argument 'path'.", Duration: sw.Elapsed));
            }

            var path = pathEl.GetString()!;
            var pattern = doc.RootElement.TryGetProperty("pattern", out var p) ? p.GetString() ?? "*" : "*";
            var entries = Directory.EnumerateFileSystemEntries(path, pattern)
                .Select(e => (Directory.Exists(e) ? "dir\t" : "file\t") + Path.GetFileName(e))
                .OrderBy(e => e)
                .Take(1000);
            return Task.FromResult(new ToolResult(invocation.InvocationId, invocation.ToolId, true,
                string.Join('\n', entries), Duration: sw.Elapsed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Task.FromResult(new ToolResult(invocation.InvocationId, invocation.ToolId, false,
                string.Empty, FailureCategory: FailureCategory.ToolFailure, Error: ex.Message, Duration: sw.Elapsed));
        }
    }
}
