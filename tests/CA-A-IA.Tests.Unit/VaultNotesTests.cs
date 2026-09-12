// CA-A-IA — Tests de la bóveda (enlaces, backlinks, búsqueda, ficheros).

using CaAIA.Application.Services;

namespace CaAIA.Tests.Unit;

public sealed class VaultNotesTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), $"ca-a-ia-vault-{Guid.NewGuid():N}");
    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public void EnsureVault_CreatesIndex_Idempotent()
    {
        var dir = VaultNotes.EnsureVault(_ws);
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "index.md")));
        VaultNotes.EnsureVault(_ws); // segunda vez no rompe
        Assert.True(File.Exists(Path.Combine(dir, "index.md")));
    }

    [Fact]
    public void IsVaultPath_ConstrainsToMdInsideVault()
    {
        var vault = VaultNotes.EnsureVault(_ws);
        Assert.True(VaultNotes.IsVaultPath(_ws, Path.Combine(vault, "nota.md")));
        Assert.False(VaultNotes.IsVaultPath(_ws, Path.Combine(_ws, "nota.md")));
        Assert.False(VaultNotes.IsVaultPath(_ws, Path.Combine(vault, "nota.txt")));
        Assert.False(VaultNotes.IsVaultPath(_ws, Path.Combine(_ws, "..", "evil.md")));
    }

    [Theory]
    [InlineData("ver [[Decision X]] y [[otra|alias]]", new[] { "decision x", "otra" })]
    [InlineData("sin enlaces", new string[0])]
    [InlineData("[[a]] [[a]] [[A]]", new[] { "a" })]
    [InlineData("roto [[sin cerrar", new string[0])]
    public void ExtractLinks_ParsesWikiLinks(string text, string[] expected)
    {
        Assert.Equal(expected, VaultNotes.ExtractLinks(text));
    }

    [Fact]
    public void FileNameFor_Sanitizes()
    {
        Assert.Equal("hola mundo.md", VaultNotes.FileNameFor("  hola  mundo  "));
        Assert.Equal("nota.md", VaultNotes.FileNameFor("   "));
        Assert.DoesNotContain("/", VaultNotes.FileNameFor("a/b\\c:d"));
        Assert.True(VaultNotes.FileNameFor(new string('x', 200)).Length <= 83);
    }

    [Fact]
    public void Backlinks_AndSearch_Work()
    {
        var vault = VaultNotes.EnsureVault(_ws);
        File.WriteAllText(Path.Combine(vault, "decision.md"), "# A\nUsamos [[sqlite]] por simplicidad.");
        File.WriteAllText(Path.Combine(vault, "sqlite.md"), "# B\nMotor embebido.");
        File.WriteAllText(Path.Combine(vault, "otro.md"), "# C\nNada que ver con jardinería.");
        var notes = VaultNotes.ReadAll(vault);
        Assert.Equal(4, notes.Count); // + index.md

        Assert.Equal(new[] { "decision" }, VaultNotes.Backlinks(notes, "sqlite"));
        Assert.Empty(VaultNotes.Backlinks(notes, "inexistente"));

        var hits = VaultNotes.Search(notes, "sqlite motor");
        Assert.Equal("sqlite", hits[0].Title);
        Assert.Empty(VaultNotes.Search(notes, "zzzznada"));
        Assert.Empty(VaultNotes.Search(notes, " "));
    }

    [Fact]
    public void WithFrontmatter_WrapsContent()
    {
        var text = VaultNotes.WithFrontmatter("Mi nota", "cuerpo");
        Assert.Contains("title: Mi nota", text);
        Assert.Contains("cuerpo", text);
    }
}
