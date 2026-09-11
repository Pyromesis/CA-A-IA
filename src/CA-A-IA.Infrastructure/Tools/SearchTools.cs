// CA-A-IA — Herramientas de búsqueda (solo lectura): por contenido y por nombre.

using System.Diagnostics;
using System.Text.Json;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Tools;

namespace CaAIA.Infrastructure.Tools;

/// <summary>
/// Busca texto en ficheros del workspace. Argumentos: { "pattern", "directory", "extensions"?, "maxResults"? }.
/// Búsqueda literal case-insensitive por líneas (sin regex: predecible y rápida).
/// </summary>
public sealed class SearchTextTool : ITool
{
    public const string ToolId = "SearchText";
    private const int DefaultMaxResults = 50;

    public ToolDefinition Definition { get; } = new(
        ToolId, "SearchText", "Searches file contents (literal, case-insensitive) under a directory.",
        ToolKind.Search, ToolPermission.Read,
        new[]
        {
            new ToolParameter("pattern", "Text to find.", "string", IsRequired: true),
            new ToolParameter("directory", "Directory to search (inside the scope).", "string", IsRequired: true),
            new ToolParameter("extensions", "Comma-separated extensions, e.g. 'cs,xaml'. Empty = all text.", "string", IsRequired: false, DefaultJson: "\"\""),
            new ToolParameter("maxResults", "Max matches.", "integer", IsRequired: false, DefaultJson: "50"),
        },
        TimeSpan.FromSeconds(60));

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var pattern = WriteFileTool.Required(doc, "pattern");
            var directory = WriteFileTool.Required(doc, "directory");
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(directory)
                || !Directory.Exists(directory))
            {
                return WriteFileTool.Fail(invocation, "Missing or invalid 'pattern'/'directory'.", sw);
            }

            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("extensions", out var e) && e.ValueKind == JsonValueKind.String)
            {
                foreach (var ext in e.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    extensions.Add(ext.Trim().TrimStart('.'));
                }
            }

            var max = doc.RootElement.TryGetProperty("maxResults", out var m) && m.TryGetInt32(out var n)
                ? Math.Clamp(n, 1, 500) : DefaultMaxResults;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(invocation.TimeoutOverride ?? Definition.DefaultTimeout);

            var matches = new List<string>();
            foreach (var file in EnumerateFiles(directory, timeoutCts.Token))
            {
                if (matches.Count >= max)
                {
                    break;
                }

                timeoutCts.Token.ThrowIfCancellationRequested();
                if (extensions.Count > 0 && !extensions.Contains(Path.GetExtension(file).TrimStart('.')))
                {
                    continue;
                }

                foreach (var hit in await GrepFileAsync(file, pattern, max - matches.Count, timeoutCts.Token)
                    .ConfigureAwait(false))
                {
                    matches.Add(hit);
                }
            }

            var output = matches.Count == 0
                ? $"No matches for '{pattern}'."
                : string.Join('\n', matches);
            return WriteFileTool.Ok(invocation, output, sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WriteFileTool.Fail(invocation, "SearchText timed out.", sw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return WriteFileTool.Fail(invocation, ex.Message, sw);
        }
    }

    internal static IEnumerable<string> EnumerateFiles(string directory, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(directory);
        var scanned = 0;
        while (stack.Count > 0 && scanned < 20_000)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            var name = Path.GetFileName(dir);
            if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".vs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (++scanned > 20_000)
                {
                    yield break;
                }

                if (Directory.Exists(entry))
                {
                    stack.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    internal static async Task<IReadOnlyList<string>> GrepFileAsync(
        string file, string pattern, int max, CancellationToken ct)
    {
        var hits = new List<string>();
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 4096, useAsync: true);
            if (stream.Length > 2_000_000)
            {
                return hits; // binarios/grandes fuera
            }

            using var reader = new StreamReader(stream);
            string? line;
            var number = 0;
            while (hits.Count < max && (line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                number++;
                if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add($"{file}:{number}: {line.Trim()}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return hits;
    }
}

/// <summary>
/// Busca ficheros por nombre (subcadena, case-insensitive). Argumentos: { "name", "directory" }.
/// </summary>
public sealed class SearchFilesTool : ITool
{
    public const string ToolId = "SearchFiles";

    public ToolDefinition Definition { get; } = new(
        ToolId, "SearchFiles", "Finds files by name substring under a directory.",
        ToolKind.Search, ToolPermission.Read,
        new[]
        {
            new ToolParameter("name", "Filename substring.", "string", IsRequired: true),
            new ToolParameter("directory", "Directory to search (inside the scope).", "string", IsRequired: true),
        },
        TimeSpan.FromSeconds(60));

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var name = WriteFileTool.Required(doc, "name");
            var directory = WriteFileTool.Required(doc, "directory");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(directory)
                || !Directory.Exists(directory))
            {
                return Task.FromResult(WriteFileTool.Fail(invocation, "Missing or invalid 'name'/'directory'.", sw));
            }

            var found = SearchTextTool.EnumerateFiles(directory, cancellationToken)
                .Where(f => Path.GetFileName(f).Contains(name, StringComparison.OrdinalIgnoreCase))
                .Take(100)
                .ToList();
            var output = found.Count == 0 ? $"No files matching '{name}'." : string.Join('\n', found);
            return Task.FromResult(WriteFileTool.Ok(invocation, output, sw));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Task.FromResult(WriteFileTool.Fail(invocation, ex.Message, sw));
        }
    }
}
