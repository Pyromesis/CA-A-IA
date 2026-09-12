// CA-A-IA — Aprendizaje continuo: la IA anota errores y correcciones y los
// consulta antes de actuar (no repetir jamás). Dos niveles:
// - Proyecto: <workspace>/.ca-a-ia/lecciones.md (viaja con el proyecto).
// - Global: %LocalAppData%/CA-A-IA/lecciones.md ("recuerda siempre:").

using CaAIA.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaAIA.Application.Services;

/// <summary>Alcance de una lección.</summary>
public enum LessonScope
{
    Project = 0,
    Global = 1,
}

/// <summary>Detecta el comando explícito "recuerda:" en mensajes del usuario.</summary>
public static class LessonIntake
{
    private static readonly (string Marker, LessonScope Scope)[] Markers =
    {
        ("recuerda siempre:", LessonScope.Global),
        ("remember always:", LessonScope.Global),
        ("nota global:", LessonScope.Global),
        ("recuerda:", LessonScope.Project),
        ("remember:", LessonScope.Project),
        ("aprende:", LessonScope.Project),
        ("learn:", LessonScope.Project),
        ("nota:", LessonScope.Project),
        ("note:", LessonScope.Project),
    };

    /// <summary>
    /// ¿Es este mensaje una lección explícita? Devuelve alcance + texto limpio.
    /// Requiere al menos 4 caracteres de contenido.
    /// </summary>
    public static bool TryExtract(string? text, out LessonScope scope, out string lesson)
    {
        scope = LessonScope.Project;
        lesson = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        foreach (var (marker, s) in Markers)
        {
            if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                var body = trimmed[marker.Length..].Trim();
                if (body.Length < 4)
                {
                    return false;
                }

                scope = s;
                lesson = body;
                return true;
            }
        }

        return false;
    }
}

/// <summary>Guarda y recupera lecciones (deduplicadas, acotadas, sin lanzar).</summary>
public sealed class LessonStore
{
    private const int MaxLessonsPerFile = 200;
    private const int MaxLessonChars = 500;

    private readonly string _globalFile;
    private readonly ILogger<LessonStore> _log;

    public LessonStore(IOptions<CaAIAOptions> options, ILogger<LessonStore> log)
    {
        _log = log;
        var configured = options.Value.Persistence.DataPath;
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CA-A-IA")
            : configured;
        _globalFile = Path.Combine(root, "lecciones.md");
    }

    /// <summary>Guarda una lección (sin duplicados). Nunca lanza.</summary>
    public async Task RecordLessonAsync(
        string? workspacePath, LessonScope scope, string text, string source,
        CancellationToken ct)
    {
        try
        {
            var clean = (text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
            while (clean.Contains("  ", StringComparison.Ordinal))
            {
                clean = clean.Replace("  ", " ", StringComparison.Ordinal);
            }

            if (clean.Length < 4)
            {
                return;
            }

            if (clean.Length > MaxLessonChars)
            {
                clean = clean[..MaxLessonChars].Trim() + "…";
            }

            var file = scope == LessonScope.Global
                ? EnsureGlobalFile()
                : ProjectLessonsFile(workspacePath);
            if (file is null)
            {
                return;
            }

            var existing = await ReadLessonsAsync(file, ct).ConfigureAwait(false);
            var normalized = clean.ToLowerInvariant();
            if (existing.Any(l => l.Normalized.Contains(normalized, StringComparison.Ordinal)
                || normalized.Contains(l.Normalized, StringComparison.Ordinal)))
            {
                return; // ya aprendido: no repetir ni duplicar
            }

            existing.Add((clean, normalized));
            while (existing.Count > MaxLessonsPerFile)
            {
                existing.RemoveAt(0);
            }

            var lines = new List<string>
            {
                "# Lecciones",
                string.Empty,
                scope == LessonScope.Global
                    ? "Lecciones globales: valen en todos los proyectos."
                    : "Lecciones del proyecto: errores y correcciones que no se repiten.",
                string.Empty,
            };
            lines.AddRange(existing.Select(l =>
                $"- [{DateTimeOffset.UtcNow:yyyy-MM-dd}] [{source}] {l.Original}"));
            await File.WriteAllLinesAsync(file, lines, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Lesson not recorded (non-fatal).");
        }
    }

    /// <summary>Recupera lecciones relevantes (proyecto + globales). Nunca lanza.</summary>
    public async Task<IReadOnlyList<string>> RecallRelevantAsync(
        string? workspacePath, string query, int max = 3, CancellationToken ct = default)
    {
        try
        {
            var notes = new List<VaultNote>();
            if (!string.IsNullOrWhiteSpace(workspacePath))
            {
                var projectFile = Path.Combine(VaultNotes.VaultDir(workspacePath), "lecciones.md");
                if (File.Exists(projectFile))
                {
                    notes.Add(new VaultNote("lecciones", projectFile,
                        await File.ReadAllTextAsync(projectFile, ct).ConfigureAwait(false),
                        Array.Empty<string>()));
                }
            }

            if (File.Exists(_globalFile))
            {
                notes.Add(new VaultNote("lecciones-globales", _globalFile,
                    await File.ReadAllTextAsync(_globalFile, ct).ConfigureAwait(false),
                    Array.Empty<string>()));
            }

            if (notes.Count == 0)
            {
                return Array.Empty<string>();
            }

            return VaultNotes.Search(notes, query, Math.Clamp(max, 1, 5))
                .SelectMany(n => LessonTexts(n.Content))
                .Select(t => t.Length > 300 ? t[..300].Trim() + "…" : t)
                .Distinct()
                .Take(Math.Clamp(max, 1, 5))
                .Select(t => "• " + t)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Lesson recall failed (non-fatal).");
            return Array.Empty<string>();
        }
    }

    private string? ProjectLessonsFile(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return null;
        }

        var dir = VaultNotes.EnsureVault(workspacePath);
        return Path.Combine(dir, "lecciones.md");
    }

    private string EnsureGlobalFile()
    {
        var dir = Path.GetDirectoryName(_globalFile)!;
        Directory.CreateDirectory(dir);
        return _globalFile;
    }

    internal static async Task<List<(string Original, string Normalized)>> ReadLessonsAsync(
        string file, CancellationToken ct)
    {
        var lessons = new List<(string, string)>();
        if (!File.Exists(file))
        {
            return lessons;
        }

        foreach (var raw in await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false))
        {
            var text = ParseLessonLine(raw);
            if (text.Length >= 4)
            {
                lessons.Add((text, text.ToLowerInvariant()));
            }
        }

        return lessons;
    }

    private static IEnumerable<string> LessonTexts(string content)
    {
        foreach (var raw in (content ?? string.Empty).Split('\n'))
        {
            var text = ParseLessonLine(raw);
            if (text.Length >= 4)
            {
                yield return text;
            }
        }
    }

    /// <summary>"- [fecha] [fuente] texto" → texto (o "" si no es lección).</summary>
    public static string ParseLessonLine(string raw)
    {
        var line = (raw ?? string.Empty).Trim();
        if (!line.StartsWith("- [", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var close = line.IndexOf(']', StringComparison.Ordinal);
        var text = (close >= 0 ? line[(close + 1)..] : line).Trim();
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            var close2 = text.IndexOf(']');
            text = (close2 >= 0 ? text[(close2 + 1)..] : text).Trim();
        }

        return text;
    }
}
