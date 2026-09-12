// CA-A-IA — Herramientas de la bóveda (.ca-a-ia/*.md estilo Obsidian):
// escribir/leer/buscar/grafo de notas con [[wiki-links]]. La autorización
// (scope + confirmación) la aplica LlmTaskExecutor; aquí además se exige
// que la ruta viva dentro de .ca-a-ia (defensa en profundidad).

using System.Diagnostics;
using System.Text.Json;
using CaAIA.Application.Services;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Tools;

namespace CaAIA.Infrastructure.Tools;

/// <summary>Guarda (o añade a) una nota .md de la bóveda. { "path", "content", "append"? }.</summary>
public sealed class VaultWriteTool : ITool
{
    public const string ToolId = "VaultWrite";
    private const int HardMaxChars = 200_000;

    public ToolDefinition Definition { get; } = new(
        ToolId, "VaultWrite", "Saves project memory: creates/updates a .md note inside .ca-a-ia (links with [[other-note]]).",
        ToolKind.FileSystem, ToolPermission.Write,
        new[]
        {
            new ToolParameter("path", "Absolute .md path inside .ca-a-ia.", "string", IsRequired: true),
            new ToolParameter("content", "Markdown content.", "string", IsRequired: true),
            new ToolParameter("append", "Append instead of overwrite.", "boolean", IsRequired: false, DefaultJson: "false"),
        },
        TimeSpan.FromSeconds(30));

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var path = WriteFileTool.Required(doc, "path");
            var content = WriteFileTool.Required(doc, "content");
            if (string.IsNullOrWhiteSpace(path) || content is null)
            {
                return WriteFileTool.Fail(invocation, "Missing required arguments 'path'/'content'.", sw);
            }

            if (content.Length > HardMaxChars)
            {
                return WriteFileTool.Fail(invocation, $"Content exceeds {HardMaxChars} chars.", sw);
            }

            var vaultDir = VaultDirFor(path);
            if (vaultDir is null)
            {
                return WriteFileTool.Fail(invocation,
                    "Path must be a .md file inside the workspace .ca-a-ia vault.", sw);
            }

            var append = doc.RootElement.TryGetProperty("append", out var a)
                && a.ValueKind == JsonValueKind.True;
            Directory.CreateDirectory(vaultDir);
            var title = Path.GetFileNameWithoutExtension(path);
            if (append && File.Exists(path))
            {
                await File.AppendAllTextAsync(path, "\n" + content.Trim() + "\n", cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(path,
                    VaultNotes.WithFrontmatter(title, content), cancellationToken).ConfigureAwait(false);
            }

            var links = VaultNotes.ExtractLinks(content);
            return WriteFileTool.Ok(invocation,
                links.Count == 0
                    ? $"Saved note {title}."
                    : $"Saved note {title} (links: {string.Join(", ", links.Take(8))}).", sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WriteFileTool.Fail(invocation, "VaultWrite timed out.", sw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return WriteFileTool.Fail(invocation, ex.Message, sw);
        }
    }

    internal static string? VaultDirFor(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(full), ".md", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var dir = Path.GetDirectoryName(full);
            if (dir is null || !Path.GetFileName(dir).Equals(VaultNotes.VaultDirName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return dir;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Lee una nota de la bóveda. { "path" }.</summary>
public sealed class VaultReadTool : ITool
{
    public const string ToolId = "VaultRead";
    private const int HardMaxChars = 50_000;

    public ToolDefinition Definition { get; } = new(
        ToolId, "VaultRead", "Reads a .md note from .ca-a-ia (project memory).",
        ToolKind.FileSystem, ToolPermission.Read,
        new[] { new ToolParameter("path", "Absolute .md path inside .ca-a-ia.", "string", IsRequired: true) },
        TimeSpan.FromSeconds(30));

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var path = WriteFileTool.Required(doc, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                return WriteFileTool.Fail(invocation, "Missing required argument 'path'.", sw);
            }

            if (VaultWriteTool.VaultDirFor(path) is null)
            {
                return WriteFileTool.Fail(invocation,
                    "Path must be a .md file inside the workspace .ca-a-ia vault.", sw);
            }

            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (content.Length > HardMaxChars)
            {
                content = content[..HardMaxChars] + "\n…[truncated]";
            }

            return WriteFileTool.Ok(invocation, content, sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WriteFileTool.Fail(invocation, "VaultRead timed out.", sw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return WriteFileTool.Fail(invocation, ex.Message, sw);
        }
    }
}

/// <summary>Busca en la bóveda por palabras. { "directory" (workspace), "query" }.</summary>
public sealed class VaultSearchTool : ITool
{
    public const string ToolId = "VaultSearch";
    public ToolDefinition Definition { get; } = new(
        ToolId, "VaultSearch", "Searches project memory (.ca-a-ia notes) by keywords, ranked.",
        ToolKind.FileSystem, ToolPermission.Read,
        new[]
        {
            new ToolParameter("directory", "Workspace path (its .ca-a-ia is searched).", "string", IsRequired: true),
            new ToolParameter("query", "Keywords.", "string", IsRequired: true),
        },
        TimeSpan.FromSeconds(30));

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var directory = WriteFileTool.Required(doc, "directory");
            var query = WriteFileTool.Required(doc, "query");
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(query))
            {
                return Task.FromResult(WriteFileTool.Fail(invocation,
                    "Missing required arguments 'directory'/'query'.", sw));
            }

            var notes = VaultNotes.ReadAll(VaultNotes.VaultDir(directory));
            if (notes.Count == 0)
            {
                return Task.FromResult(WriteFileTool.Ok(invocation,
                    "Vault is empty: no notes yet. Create some with VaultWrite.", sw));
            }

            var hits = VaultNotes.Search(notes, query);
            if (hits.Count == 0)
            {
                return Task.FromResult(WriteFileTool.Ok(invocation,
                    $"No notes match '{query}'. Notes: {string.Join(", ", notes.Select(n => n.Title).Take(10))}.", sw));
            }

            var lines = hits.Select(n =>
                $"- {n.Title} ({n.Path}): {Snippet(n.Content, query)}" +
                (n.Links.Count > 0 ? $" [links: {string.Join(", ", n.Links.Take(5))}]" : string.Empty));
            return Task.FromResult(WriteFileTool.Ok(invocation, string.Join("\n", lines), sw));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Task.FromResult(WriteFileTool.Fail(invocation, ex.Message, sw));
        }
    }

    internal static string Snippet(string content, string query)
    {
        var text = (content ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        var first = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var at = string.IsNullOrEmpty(first) ? 0 : text.IndexOf(first, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            at = 0;
        }

        var start = Math.Max(0, at - 40);
        var snippet = text.Substring(start, Math.Min(140, text.Length - start)).Trim();
        return (start > 0 ? "…" : string.Empty) + snippet
            + (start + 140 < text.Length ? "…" : string.Empty);
    }
}

/// <summary>Red de una nota: a quién enlaza y quién la enlaza. { "directory", "note" }.</summary>
public sealed class VaultGraphTool : ITool
{
    public const string ToolId = "VaultGraph";
    public ToolDefinition Definition { get; } = new(
        ToolId, "VaultGraph", "Shows a note's network: outgoing [[links]] and backlinks.",
        ToolKind.FileSystem, ToolPermission.Read,
        new[]
        {
            new ToolParameter("directory", "Workspace path.", "string", IsRequired: true),
            new ToolParameter("note", "Note title.", "string", IsRequired: true),
        },
        TimeSpan.FromSeconds(30));

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var directory = WriteFileTool.Required(doc, "directory");
            var note = WriteFileTool.Required(doc, "note");
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(note))
            {
                return Task.FromResult(WriteFileTool.Fail(invocation,
                    "Missing required arguments 'directory'/'note'.", sw));
            }

            var notes = VaultNotes.ReadAll(VaultNotes.VaultDir(directory));
            var mine = notes.FirstOrDefault(n =>
                n.Title.Equals(note.Trim(), StringComparison.OrdinalIgnoreCase));
            if (mine is null)
            {
                return Task.FromResult(WriteFileTool.Ok(invocation,
                    $"Note '{note}' not found. Notes: {string.Join(", ", notes.Select(n => n.Title).Take(10))}.", sw));
            }

            var backlinks = VaultNotes.Backlinks(notes, mine.Title);
            return Task.FromResult(WriteFileTool.Ok(invocation,
                $"'{mine.Title}' links to: {(mine.Links.Count == 0 ? "(none)" : string.Join(", ", mine.Links))}\n" +
                $"Linked from: {(backlinks.Count == 0 ? "(none)" : string.Join(", ", backlinks))}", sw));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Task.FromResult(WriteFileTool.Fail(invocation, ex.Message, sw));
        }
    }
}
