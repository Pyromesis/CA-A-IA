// CA-A-IA — Texto de actividad del agente: traduce herramientas/estados a frases humanas
// para el feed en vivo del Chat ("Leyendo X…", "Compilando…"). Puro y testeable (sin UI).

using System.Text.Json;

namespace CaAIA.Application.Services;

/// <summary>
/// Mapeo evento→frase de actividad. El Chat lo usa para la línea de estado en vivo;
/// los hitos (tareas, final) siguen como burbujas.
/// </summary>
public static class AgentActivityText
{
    /// <summary>Frase al empezar una herramienta, o null si no merece línea de estado.</summary>
    public static string? ForToolStarted(string toolId, string? argumentsJson)    {
        var args = ParseArgs(argumentsJson);
        return toolId switch
        {
            "ReadFile" => $"Leyendo {Short(Args(args, "path"))}…",
            "ListDirectory" => $"Explorando {Short(Args(args, "path"))}…",
            "SearchText" => $"Buscando '{Args(args, "pattern")}'…",
            "SearchFiles" => $"Buscando archivos '{Args(args, "name")}'…",
            "WriteFile" => $"Escribiendo {Short(Args(args, "path"))}…",
            "EditFile" => $"Editando {Short(Args(args, "path"))}…",
            "ExecuteCommand" => ForCommand(Args(args, "command"), Args(args, "args")),
            "OpenCode" => "Trabajando en OpenCode…",
            _ => $"Ejecutando {toolId}…",
        };
    }

    /// <summary>
    /// Burbuja para el historial cuando una herramienta mutadora termina (qué archivo tocó).
    /// Null para lecturas/búsquedas (ruido) o herramientas desconocidas.
    /// </summary>
    public static string? ForToolCompleted(string toolId, string? argumentsJson, bool ok, string? error)
    {
        var args = ParseArgs(argumentsJson);
        if (!ok)
        {
            var what = toolId switch
            {
                "WriteFile" => $"escribir {Tail(Args(args, "path"))}",
                "EditFile" => $"editar {Tail(Args(args, "path"))}",
                "ExecuteCommand" => $"ejecutar {Tail(CommandLine(args))}",
                _ => null,
            };
            return what is null ? null : $"⚠ No se pudo {what}: {Tail(error ?? string.Empty, 140)}";
        }

        return toolId switch
        {
            "WriteFile" => $"✎ Escribió {Tail(Args(args, "path"))}",
            "EditFile" => $"✎ Editó {Tail(Args(args, "path"))}",
            "ExecuteCommand" => $"▶ Ejecutó {Tail(CommandLine(args))}",
            _ => null,
        };
    }

    private static string CommandLine(Dictionary<string, string> args)
    {
        var command = Args(args, "command");
        var rest = Args(args, "args");
        return $"{command} {rest}".Trim();
    }

    /// <summary>Cola de un texto (con … delante si recorta): lo interesante va al final.</summary>
    private static string Tail(string value, int max = 90)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length <= max)
        {
            return text;
        }

        return "…" + text[^Math.Max(0, max - 1)..];
    }

    /// <summary>Frase para estados del motor (parte "To" de "From -> To: reason").</summary>
    public static string? ForState(string? toState) => toState switch
    {
        "Executing" => "Trabajando…",
        "Testing" or "Retesting" => "Probando…",
        "VerifyingTask" => "Verificando…",
        "AnalyzingFailure" => "Analizando el fallo…",
        "Repairing" => "Solucionando…",
        "AdvancingTask" => "Siguiente tarea…",
        "FinalVerification" => "Auditoría final…",
        "Planning" => "Planificando…",
        "Understanding" or "InspectingProject" => "Analizando…",
        _ => null,
    };

    private static string ForCommand(string command, string args)
    {
        var full = $"{command} {args}".Trim().ToLowerInvariant();
        if (full.Contains("test"))
        {
            return "Pasando pruebas…";
        }

        if (full.Contains("build") || full.Contains("compile") || full.Contains("dotnet")
            || full.Contains("msbuild") || full.Contains("cargo build") || full.Contains("npm run build"))
        {
            return "Compilando…";
        }

        if (full.Contains("git"))
        {
            return "Consultando git…";
        }

        return string.IsNullOrWhiteSpace(command) ? "Ejecutando comando…" : $"Ejecutando {command}…";
    }

    private static Dictionary<string, string> ParseArgs(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                    JsonValueKind.Array => string.Join(" ", prop.Value.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString() ?? string.Empty)),
                    _ => prop.Value.ToString(),
                };
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    private static string Args(Dictionary<string, string> args, string name) =>
        args.TryGetValue(name, out var value) ? value : string.Empty;

    private static string Short(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "…";
        }

        try
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch (Exception)
        {
            return "…";
        }
    }
}
