// CA-A-IA · Fase 0 — Descubrimiento de archivos relevantes (base de la estrategia de contexto, §16).

using CaAIA.Domain.Context;

namespace CaAIA.Infrastructure.FileSystem;

/// <summary>
/// Enumera ficheros de código/config relevantes ignorando artefactos (bin/obj/.git/.vs/...).
/// TODO(FUTURE_PHASE): ranking por relevancia y caché por hash.
/// </summary>
public sealed class WorkspaceReader
{
    private static readonly string[] IgnoredDirs =
        { ".git", ".vs", "bin", "obj", "node_modules", "TestResults", ".idea", ".vscode" };

    private static readonly string[] RelevantExtensions =
    {
        ".cs", ".xaml", ".csproj", ".slnx", ".sln", ".json", ".xml", ".md",
        ".ps1", ".props", ".targets",
        // Texto y código habitual: sin esto, una carpeta con .txt parecía vacía.
        ".txt", ".log", ".csv", ".tsv", ".ini", ".cfg", ".toml", ".yaml", ".yml",
        ".py", ".js", ".ts", ".tsx", ".jsx", ".html", ".css", ".scss", ".java",
        ".go", ".rs", ".cpp", ".h", ".hpp", ".c", ".sql", ".sh", ".bat", ".cmd",
    };

    /// <summary>
    /// Ficheros que nunca entran al contexto (aunque tengan extensión relevante):
    /// secretos y bases de datos locales que no deben llegar a un LLM remoto.
    /// </summary>
    internal static readonly string[] SecretFileNames =
        { ".env", "secrets.dat", "ca-a-ia.db", "id_rsa", "id_ed25519" };

    internal static bool IsSecretFile(string fileName)
    {
        if (fileName.EndsWith(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SecretFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Descubrimiento cancelable y acotado: ni la enumeración de un árbol gigante puede
    /// bloquear al llamador indefinidamente (el UI lo invoca en background + cancela).
    /// </summary>
    public IReadOnlyList<string> DiscoverRelevantFiles(
        string workspacePath, int maxFiles = 500, int maxScannedEntries = 50_000,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(workspacePath))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(workspacePath);
        var scanned = 0;

        while (stack.Count > 0 && result.Count < maxFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            string name = Path.GetFileName(dir);
            if (IgnoredDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir, "*",
                    new EnumerationOptions
                    {
                        // No seguir symlinks/junctions: evita escapes del workspace y ciclos.
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (result.Count >= maxFiles || ++scanned > maxScannedEntries)
                {
                    return result;
                }

                if ((scanned & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // Doble guarda TOCTOU/symlink: AttributesToSkip no cubre todas las
                // variantes (junctions); re-verificar atributos antes de entrar/leer.
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    stack.Push(entry);
                }
                else if (RelevantExtensions.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase)
                    && !IsSecretFile(Path.GetFileName(entry)))
                {
                    result.Add(entry);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Construye fragmentos priorizados hasta maxChars: binarios fuera, tope por fichero
    /// (25k chars con marca de truncado) para que un solo fichero no devore el presupuesto.
    /// </summary>
    public async Task<ProjectContext> ReadFragmentsAsync(
        string workspacePath, IReadOnlyList<string> hints, int maxChars, CancellationToken ct)
    {
        const int perFileCap = 25_000;
        var files = DiscoverRelevantFiles(workspacePath);
        var ordered = files
            .OrderByDescending(f => hints.Any(h => f.Contains(h, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(f => f.Length)
            .ToList();

        var fragments = new List<ContextFragment>();
        var total = 0;
        var truncated = 0;
        foreach (var file in ordered)
        {
            ct.ThrowIfCancellationRequested();
            string? content;
            try
            {
                content = await ReadTextCappedAsync(file, perFileCap, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                continue;
            }

            if (content is null)
            {
                continue; // binario
            }

            if (total + content.Length > maxChars)
            {
                truncated++;
                continue;
            }

            var priority = hints.Any(h => file.Contains(h, StringComparison.OrdinalIgnoreCase)) ? 10 : 1;
            fragments.Add(new ContextFragment("file", file, content, priority));
            total += content.Length;
        }

        return new ProjectContext(workspacePath, fragments, total, truncated);
    }

    /// <summary>Lee texto hasta el tope; devuelve null si parece binario (NUL en el primer bloque).</summary>
    internal static async Task<string?> ReadTextCappedAsync(string path, int cap, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
        using var reader = new StreamReader(stream);
        char[] block = new char[Math.Min(4096, cap + 1)];
        var sb = new System.Text.StringBuilder();
        int read;
        var first = true;
        while (sb.Length <= cap && (read = await reader.ReadAsync(block, ct).ConfigureAwait(false)) > 0)
        {
            if (first)
            {
                first = false;
                for (var i = 0; i < read; i++)
                {
                    if (block[i] == '\0')
                    {
                        return null;
                    }
                }
            }

            sb.Append(block, 0, read);
        }

        if (sb.Length > cap)
        {
            sb.Length = cap;
            sb.Append("\n…[truncated:file-capped]");
        }

        return sb.ToString();
    }
}
