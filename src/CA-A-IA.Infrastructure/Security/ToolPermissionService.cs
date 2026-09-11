// CA-A-IA · Fase 0 — Autorización de herramientas (§15). El agente es NO confiable: todo se valida.

using CaAIA.Domain.Enums;
using CaAIA.Domain.Security;
using CaAIA.Domain.Tools;

namespace CaAIA.Infrastructure.Security;

/// <summary>
/// Reglas: 1) la herramienta debe estar dentro de los permisos concedidos; 2) toda ruta de
/// escritura/ejecución debe estar en <see cref="ExecutionScope"/>; 3) las denegadas vetan.
/// </summary>
public sealed class ToolPermissionService : IToolPermissionService
{
    public PermissionDecision Authorize(ToolInvocation invocation, ToolDefinition definition, ExecutionScope scope)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(scope);

        if ((definition.RequiredPermissions & ~scope.GrantedPermissions) != ToolPermission.None)
        {
            return new PermissionDecision(false,
                $"Tool '{definition.Id}' requires {definition.RequiredPermissions} but scope grants {scope.GrantedPermissions}.",
                RequiresUserConfirmation: false);
        }

        if (RequiresPathCheck(definition.RequiredPermissions))
        {
            var paths = ExtractPaths(invocation.ArgumentsJson);
            foreach (var path in paths)
            {
                if (!scope.IsPathAllowed(path))
                {
                    return new PermissionDecision(false,
                        $"Path '{path}' is outside the allowed execution scope.", RequiresUserConfirmation: false);
                }
            }

            // Solo escritura/ejecución piden confirmación; la lectura se autoriza sin
            // interrumpir (pero sus rutas ya quedaron validadas contra el scope arriba).
            var needsConfirmation =
                (definition.RequiredPermissions & (ToolPermission.Write | ToolPermission.Delete)) != 0
                    ? scope.RequireConfirmationForWrite
                    : (definition.RequiredPermissions & ToolPermission.Execute) != 0
                        ? scope.RequireConfirmationForExecute
                        : false;

            if (needsConfirmation && paths.Count > 0)
            {
                return new PermissionDecision(true, "Allowed pending user confirmation.", RequiresUserConfirmation: true);
            }
        }

        return new PermissionDecision(true, "Allowed.", RequiresUserConfirmation: false);
    }

    private static bool RequiresPathCheck(ToolPermission permissions) =>
        (permissions & (ToolPermission.Read | ToolPermission.Write | ToolPermission.Delete | ToolPermission.Execute)) != 0;

    /// <summary>
    /// Extracción best-effort de rutas desde el JSON de argumentos (propiedades "path"/"paths"/"file"/"directory"/"workdir"…,
    /// más elementos de "args" que parezcan rutas absolutas para que ExecuteCommand no escape del scope).
    /// TODO(FUTURE_PHASE): validación contra el esquema declarado de cada herramienta.
    /// </summary>
    internal static IReadOnlyList<string> ExtractPaths(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            var found = new List<string>();
            CollectPaths(doc.RootElement, found);
            return found;
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static void CollectPaths(System.Text.Json.JsonElement el, List<string> found)
    {
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
                        prop.Name is "path" or "paths" or "file" or "directory" or "directoryPath" or "filePath" or "workdir")
                    {
                        var v = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            found.Add(v);
                        }
                    }
                    else if (prop.Name == "args" && prop.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        // ExecuteCommand: un argumento absoluto fuera del workspace también es escape.
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                var v = item.GetString();
                                if (!string.IsNullOrWhiteSpace(v) && LooksLikeRootedPath(v))
                                {
                                    found.Add(v);
                                }
                            }
                        }
                    }
                    else
                    {
                        CollectPaths(prop.Value, found);
                    }
                }

                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    CollectPaths(item, found);
                }

                break;
        }
    }

    private static bool LooksLikeRootedPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var v = value.Trim().Trim('"');
        try
        {
            return Path.IsPathRooted(v);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
