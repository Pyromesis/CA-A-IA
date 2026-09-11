// CA-A-IA · Fase 0 — Registro de herramientas + herramientas de lectura reales (no destructivas).

using System.Collections.Concurrent;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Tools;

namespace CaAIA.Infrastructure.Tools;

/// <summary>Catálogo thread-safe de herramientas (§14).</summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!_tools.TryAdd(tool.Definition.Id, tool))
        {
            throw new InvalidOperationException($"Tool '{tool.Definition.Id}' is already registered.");
        }
    }

    public ITool Get(string toolId)
    {
        if (_tools.TryGetValue(toolId, out var tool))
        {
            return tool;
        }

        throw new KeyNotFoundException($"Tool '{toolId}' is not registered.");
    }

    public IReadOnlyCollection<ToolDefinition> ListDefinitions() =>
        _tools.Values.Select(t => t.Definition).ToList();

    public IReadOnlyCollection<ToolDefinition> ListDefinitionsFor(ToolPermission granted) =>
        _tools.Values
            .Select(t => t.Definition)
            .Where(d => (d.RequiredPermissions & ~granted) == ToolPermission.None)
            .ToList();
}
