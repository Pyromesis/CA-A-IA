// CA-A-IA — Tests del árbol de archivos (estructura, orden, recuentos, tamaños).

using CaAIA.Application.Services;

namespace CaAIA.Tests.Unit;

public sealed class FileTreeTests
{
    private static string W(params string[] parts) => Path.Combine(parts);

    [Fact]
    public void Build_NestsFolders_FoldersFirst_Sorted()
    {
        var ws = W(Path.GetTempPath(), "ws");
        var tree = FileTreeBuilder.Build(ws, new[]
        {
            W(ws, "zebra.txt"),
            W(ws, "src", "b.cs"),
            W(ws, "src", "a.cs"),
            W(ws, "docs", "readme.md"),
        });

        Assert.Equal("ws", tree.Item.Name);
        Assert.True(tree.Item.IsDirectory);
        Assert.Equal(4, tree.Item.DescendantFiles);

        // Carpetas primero y ordenadas, luego archivos.
        Assert.Equal(new[] { "docs", "src", "zebra.txt" },
            tree.Children.Select(c => c.Item.Name).ToArray());

        var src = tree.Children.Single(c => c.Item.Name == "src");
        Assert.Equal(new[] { "a.cs", "b.cs" }, src.Children.Select(c => c.Item.Name).ToArray());
        Assert.Equal(2, src.Item.DescendantFiles);
        Assert.Equal("2 archivos", src.Item.Meta);
        Assert.True(tree.Children.All(c => c.Item.Meta.Length > 0));
    }

    [Fact]
    public void Build_SkipsOutsideWorkspace()
    {
        var ws = W(Path.GetTempPath(), "ws");
        var tree = FileTreeBuilder.Build(ws, new[]
        {
            W(Path.GetTempPath(), "other", "evil.txt"),
            W(ws, "ok.txt"),
        });
        Assert.Single(tree.Children);
        Assert.Equal("ok.txt", tree.Children[0].Item.Name);
        Assert.Equal(1, tree.Item.DescendantFiles);
    }

    [Fact]
    public void Build_Empty_ReturnsRootWithZero()
    {
        var tree = FileTreeBuilder.Build(W(Path.GetTempPath(), "ws"), Array.Empty<string>());
        Assert.Empty(tree.Children);
        Assert.Equal(0, tree.Item.DescendantFiles);
        Assert.Equal("0 archivos", tree.Item.Meta);
    }

    [Theory]
    [InlineData(-1, "—")]
    [InlineData(0, "0 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(2 * 1024 * 1024, "2.0 MB")]
    public void FormatSize_Formats(long bytes, string expected)
    {
        Assert.Equal(expected, FileTreeBuilder.FormatSize(bytes));
    }

    [Fact]
    public void Item_Meta_ShowsSizeForFiles()
    {
        var item = new FileTreeItem("a.txt", "C:\\x\\a.txt", IsDirectory: false, SizeBytes: 12);
        Assert.Equal("12 B", item.Meta);
    }
}
