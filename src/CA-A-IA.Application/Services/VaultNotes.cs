// CA-A-IA — Bóveda de conocimiento por proyecto (estilo Obsidian):
// <workspace>/.ca-a-ia/*.md con [[wiki-links]]. La IA la lee para contexto,
// la escribe para recordar decisiones y la recorre como red (backlinks).
// Puro y testeable: opera sobre un directorio dado.

namespace CaAIA.Application.Services;

/// <summary>Nota de la bóveda con sus enlaces salientes.</summary>
public sealed record VaultNote(string Title, string Path, string Content, IReadOnlyList<string> Links);

/// <summary>Utilidades de la bóveda (rutas, enlaces, búsqueda). Sin E/S salvo la indicada.</summary>
public static class VaultNotes
{
    public const string VaultDirName = ".ca-a-ia";

    public static string VaultDir(string workspacePath) =>
        Path.Combine(workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), VaultDirName);

    /// <summary>Crea la bóveda + index.md si falta. Idempotente.</summary>
    public static string EnsureVault(string workspacePath)
    {
        var dir = VaultDir(workspacePath);
        Directory.CreateDirectory(dir);
        var index = Path.Combine(dir, "index.md");
        if (!File.Exists(index))
        {
            File.WriteAllText(index,
                $"""
                ---
                title: index
                updated: {DateTimeOffset.UtcNow:O}
                ---

                # Índice de la bóveda

                Memoria del proyecto: decisiones, aprendizajes y contexto que la IA
                consulta con VaultSearch/VaultRead y amplía con VaultWrite.
                Enlaza notas con [[titulo-de-otra-nota]].
                """);
        }

        return dir;
    }

    /// <summary>¿Vive esta ruta dentro de la bóveda del workspace? (defensa en profundidad).</summary>
    public static bool IsVaultPath(string workspacePath, string path)
    {
        string full, vault;
        try
        {
            full = Path.GetFullPath(path);
            vault = Path.GetFullPath(VaultDir(workspacePath));
        }
        catch (Exception)
        {
            return false;
        }

        if (!string.Equals(Path.GetExtension(full), ".md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = vault.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Título → nombre de fichero seguro (sin subcarpetas).</summary>
    public static string FileNameFor(string title)
    {
        var clean = (title ?? string.Empty).Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            clean = clean.Replace(c, '-');
        }

        clean = clean.Replace('/', '-').Replace('\\', '-');
        while (clean.Contains("  ", StringComparison.Ordinal))
        {
            clean = clean.Replace("  ", " ", StringComparison.Ordinal);
        }

        clean = clean.Trim().Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(clean))
        {
            clean = "nota";
        }

        if (clean.Length > 80)
        {
            clean = clean[..80].Trim();
        }

        return clean + ".md";
    }

    /// <summary>Enlaces [[wiki]] del texto (normalizados a minúsculas).</summary>
    public static IReadOnlyList<string> ExtractLinks(string? text)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return found;
        }

        var span = text.AsSpan();
        while (true)
        {
            var open = span.IndexOf("[[", StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            var rest = span[(open + 2)..];
            var close = rest.IndexOf("]]", StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            var link = rest[..close].ToString().Trim().ToLowerInvariant();
            var pipe = link.IndexOf('|');
            if (pipe >= 0)
            {
                link = link[..pipe].Trim();
            }

            if (link.Length > 0 && !found.Contains(link, StringComparer.Ordinal))
            {
                found.Add(link);
            }

            span = rest[(close + 2)..];
        }

        return found;
    }

    /// <summary>Lee todas las notas de la bóveda (sin lanzar si no existe).</summary>
    public static IReadOnlyList<VaultNote> ReadAll(string vaultDir)
    {
        var notes = new List<VaultNote>();
        string[] files;
        try
        {
            files = Directory.Exists(vaultDir)
                ? Directory.GetFiles(vaultDir, "*.md", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
        }
        catch (Exception)
        {
            return notes;
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (Exception)
            {
                continue;
            }

            notes.Add(new VaultNote(
                Path.GetFileNameWithoutExtension(file), file, content, ExtractLinks(content)));
        }

        return notes;
    }

    /// <summary>Backlinks: notas que enlazan a este título.</summary>
    public static IReadOnlyList<string> Backlinks(IReadOnlyList<VaultNote> notes, string title)
    {
        var key = (title ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length == 0)
        {
            return Array.Empty<string>();
        }

        return notes
            .Where(n => !n.Title.Equals(title, StringComparison.OrdinalIgnoreCase)
                && n.Links.Contains(key, StringComparer.Ordinal))
            .Select(n => n.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Búsqueda por palabras: título pesa ×3, luego frecuencia. Tope 10.</summary>
    public static IReadOnlyList<VaultNote> Search(IReadOnlyList<VaultNote> notes, string query, int max = 10)
    {
        var terms = (query ?? string.Empty)
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length > 1)
            .Distinct()
            .ToList();
        if (terms.Count == 0)
        {
            return Array.Empty<VaultNote>();
        }

        return notes
            .Select(n =>
            {
                var title = n.Title.ToLowerInvariant();
                var body = n.Content.ToLowerInvariant();
                var score = terms.Sum(t =>
                    (title.Contains(t, StringComparison.Ordinal) ? 3 : 0) + Count(body, t));
                return (Note: n, Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Note.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(max, 1, 50))
            .Select(x => x.Note)
            .ToList();
    }

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>Envuelve el contenido con frontmatter (título + fecha).</summary>
    public static string WithFrontmatter(string title, string content) =>
        $"""
        ---
        title: {title.Trim()}
        updated: {DateTimeOffset.UtcNow:O}
        ---

        {(content ?? string.Empty).Trim() + "\n"}
        """;
}
