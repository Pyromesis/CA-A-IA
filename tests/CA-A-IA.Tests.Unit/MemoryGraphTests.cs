// CA-A-IA — Tests de la red de memoria (nodos, aristas, layout).

using CaAIA.Application.Services;

namespace CaAIA.Tests.Unit;

public sealed class MemoryGraphTests
{
    private static VaultNote Note(string title, string content, string? path = null) =>
        new(title, path ?? $"C:\\w\\{title}.md", content, VaultNotes.ExtractLinks(content));

    [Fact]
    public void Build_CreatesNodesAndBidirectionalEdges()
    {
        var graph = MemoryGraphBuilder.Build(new[]
        {
            Note("decision", "Usamos [[sqlite]] siempre."),
            Note("sqlite", "Motor embebido."),
            Note("suelta", "Sin enlaces."),
        }, null);

        Assert.Equal(3, graph.Nodes.Count);
        Assert.Single(graph.Edges);
        var edge = graph.Edges[0];
        Assert.Contains(edge.FromId, new[] { "decision", "sqlite" });
        Assert.Contains(edge.ToId, new[] { "decision", "sqlite" });
        Assert.NotEqual(edge.FromId, edge.ToId);
        var decision = graph.Nodes.Single(n => n.Id == "decision");
        Assert.Equal(1, decision.Degree);
        Assert.Equal(MemoryNodeKind.Note, decision.Kind);
    }

    [Fact]
    public void Build_MarksLessonsAndGlobal()
    {
        var graph = MemoryGraphBuilder.Build(
            new[] { Note("lecciones", "No repetir [[sqlite]]."), Note("sqlite", "x") },
            new VaultNote("lecciones-globales", "C:\\g.md", "Global.", Array.Empty<string>()));
        Assert.Equal(MemoryNodeKind.Lesson,
            graph.Nodes.Single(n => n.Id == "lecciones").Kind);
        Assert.Equal(MemoryNodeKind.Global,
            graph.Nodes.Single(n => n.Id == "lecciones-globales").Kind);
    }

    [Fact]
    public void Build_SkipsMissingLinkTargets()
    {
        var graph = MemoryGraphBuilder.Build(
            new[] { Note("a", "Ver [[fantasma]].") }, null);
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void Layout_IsDeterministic_AndInsideCanvas()
    {
        var notes = Enumerable.Range(0, 12)
            .Select(i => Note($"n{i}", i == 0 ? "Ver [[n1]] [[n2]]." : "x"))
            .ToList();
        var first = MemoryGraphBuilder.Build(notes, null);
        var second = MemoryGraphBuilder.Build(notes, null);
        Assert.Equal(first.Nodes.Select(n => (n.X, n.Y)), second.Nodes.Select(n => (n.X, n.Y)));
        foreach (var node in first.Nodes)
        {
            Assert.InRange(node.X, 0, MemoryGraphBuilder.CanvasWidth);
            Assert.InRange(node.Y, 0, MemoryGraphBuilder.CanvasHeight);
        }
    }

    [Fact]
    public void Snippet_StripsFrontmatter()
    {
        Assert.Equal("hola mundo",
            MemoryGraphBuilder.Snippet("---\ntitle: x\n---\n\nhola mundo"));
    }
}
