// CA-A-IA — Red de memoria: nodos (notas .md) + aristas ([[links]] y backlinks)
// con layout determinista. Puro y testeable; Presentation lo pinta arrastrable.

namespace CaAIA.Application.Services;

/// <summary>Clase de nodo para pintarlo distinto.</summary>
public enum MemoryNodeKind
{
    Note = 0,
    Lesson = 1,
    Global = 2,
}

/// <summary>Nodo posicionado en un canvas lógico de 1000×700.</summary>
public sealed record MemoryGraphNode(
    string Id,
    string Title,
    MemoryNodeKind Kind,
    string Snippet,
    string FullPath,
    int Degree,
    string Meta,
    double X,
    double Y);

/// <summary>Arista no dirigida entre dos nodos (por id).</summary>
public sealed record MemoryGraphEdge(string FromId, string ToId);

/// <summary>Grafo listo para pintar.</summary>
public sealed record MemoryGraph(
    IReadOnlyList<MemoryGraphNode> Nodes,
    IReadOnlyList<MemoryGraphEdge> Edges);

/// <summary>Construye el grafo desde notas (+ global opcional) con layout fijo.</summary>
public static class MemoryGraphBuilder
{
    public const double CanvasWidth = 1000;
    public const double CanvasHeight = 700;

    public static MemoryGraph Build(IReadOnlyList<VaultNote> notes, VaultNote? globalNote)
    {
        var all = new List<(VaultNote Note, MemoryNodeKind Kind)>();
        foreach (var note in notes)
        {
            var kind = note.Title.Equals("lecciones", StringComparison.OrdinalIgnoreCase)
                ? MemoryNodeKind.Lesson
                : MemoryNodeKind.Note;
            all.Add((note, kind));
        }

        if (globalNote is not null)
        {
            all.Add((globalNote, MemoryNodeKind.Global));
        }

        var byTitle = all
            .GroupBy(x => x.Note.Title.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var nodes = all.Select(x => new
        {
            Id = x.Note.Title.Trim().ToLowerInvariant(),
            Node = x.Note,
            x.Kind,
        }).ToList();

        var edgeSet = new HashSet<(string, string)>();
        foreach (var entry in nodes)
        {
            foreach (var link in entry.Node.Links)
            {
                var target = link.Trim().ToLowerInvariant();
                if (target.Length == 0 || target == entry.Id || !byTitle.ContainsKey(target))
                {
                    continue;
                }

                var a = entry.Id;
                var b = target;
                if (string.Compare(a, b, StringComparison.Ordinal) > 0)
                {
                    (a, b) = (b, a);
                }

                edgeSet.Add((a, b));
            }
        }

        var degree = nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
        foreach (var (a, b) in edgeSet)
        {
            degree[a]++;
            degree[b]++;
        }

        var positioned = Layout(nodes.Select(n => n.Id).ToList(), edgeSet);
        return new MemoryGraph(
            nodes.Select(n =>
            {
                var meta = n.Kind == MemoryNodeKind.Note
                    ? string.Empty
                    : n.Kind == MemoryNodeKind.Global
                        ? "global · vale en todos los proyectos"
                        : $"{degree[n.Id]} {(degree[n.Id] == 1 ? "enlace" : "enlaces")}";
                return new MemoryGraphNode(
                    n.Id, n.Node.Title, n.Kind, Snippet(n.Node.Content), n.Node.Path,
                    degree[n.Id], meta, positioned[n.Id].X, positioned[n.Id].Y);
            }).ToList(),
            edgeSet.Select(e => new MemoryGraphEdge(e.Item1, e.Item2)).ToList());
    }

    public static string Snippet(string content)
    {
        var text = (content ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        // Sin frontmatter para la vista previa.
        if (text.StartsWith("---", StringComparison.Ordinal))
        {
            var end = text.IndexOf("---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                text = text[(end + 3)..].Trim();
            }
        }

        return text.Length <= 140 ? text : text[..140].Trim() + "…";
    }

    /// <summary>Layout determinista (semilla fija): círculo si ≤6, fuerzas si más.</summary>
    internal static Dictionary<string, (double X, double Y)> Layout(
        IReadOnlyList<string> ids, IReadOnlySet<(string, string)> edges)
    {
        const double margin = 90;
        var result = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return result;
        }

        if (ids.Count == 1)
        {
            result[ids[0]] = (CanvasWidth / 2, CanvasHeight / 2);
            return result;
        }

        var random = new Random(42);
        var pos = ids.ToDictionary(id => id,
            id => (X: margin + random.NextDouble() * (CanvasWidth - 2 * margin),
                   Y: margin + random.NextDouble() * (CanvasHeight - 2 * margin)),
            StringComparer.Ordinal);
        if (ids.Count <= 6)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                var angle = 2 * Math.PI * i / ids.Count - Math.PI / 2;
                pos[ids[i]] = (CanvasWidth / 2 + 300 * Math.Cos(angle),
                               CanvasHeight / 2 + 220 * Math.Sin(angle));
            }

            return ClampAll(pos, margin);
        }

        // Fuerzas: repulsión todos-contra-todos + muelles en aristas + gravedad.
        var adjacency = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (a, b) in edges)
        {
            adjacency[a].Add(b);
            adjacency[b].Add(a);
        }

        const double repulsion = 26000;
        const double spring = 0.012;
        const double ideal = 190;
        const double gravity = 0.008;
        for (var iter = 0; iter < 140; iter++)
        {
            var force = ids.ToDictionary(id => id, _ => (X: 0.0, Y: 0.0), StringComparer.Ordinal);
            for (var i = 0; i < ids.Count; i++)
            {
                for (var j = i + 1; j < ids.Count; j++)
                {
                    var a = pos[ids[i]];
                    var b = pos[ids[j]];
                    var dx = a.X - b.X;
                    var dy = a.Y - b.Y;
                    var dist = Math.Max(30, Math.Sqrt(dx * dx + dy * dy));
                    var push = repulsion / (dist * dist);
                    var fx = push * dx / dist;
                    var fy = push * dy / dist;
                    force[ids[i]] = (force[ids[i]].X + fx, force[ids[i]].Y + fy);
                    force[ids[j]] = (force[ids[j]].X - fx, force[ids[j]].Y - fy);
                }
            }

            foreach (var (a, b) in edges)
            {
                var pa = pos[a];
                var pb = pos[b];
                var dx = pb.X - pa.X;
                var dy = pb.Y - pa.Y;
                var dist = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                var pull = spring * (dist - ideal);
                var fx = pull * dx / dist;
                var fy = pull * dy / dist;
                force[a] = (force[a].X + fx, force[a].Y + fy);
                force[b] = (force[b].X - fx, force[b].Y - fy);
            }

            var cooling = 1.0 - (double)iter / 140;
            foreach (var id in ids)
            {
                var p = pos[id];
                var step = 0.5 + 2.5 * cooling;
                var nx = p.X + Math.Clamp(force[id].X, -40, 40) * step * 0.25
                    + (CanvasWidth / 2 - p.X) * gravity;
                var ny = p.Y + Math.Clamp(force[id].Y, -40, 40) * step * 0.25
                    + (CanvasHeight / 2 - p.Y) * gravity;
                pos[id] = (nx, ny);
            }
        }

        return ClampAll(pos, margin);
    }

    private static Dictionary<string, (double X, double Y)> ClampAll(
        Dictionary<string, (double X, double Y)> pos, double margin)
    {
        foreach (var id in pos.Keys.ToList())
        {
            pos[id] = (Math.Clamp(pos[id].X, margin, CanvasWidth - margin),
                       Math.Clamp(pos[id].Y, margin, CanvasHeight - margin));
        }

        return pos;
    }
}
