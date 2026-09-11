// CA-A-IA — Tests de SearchText/SearchFiles sobre workspace temporal.

using CaAIA.Domain.Correlation;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Tools;
using CaAIA.Infrastructure.Tools;

namespace CaAIA.Tests.Unit;

public sealed class SearchToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ca-a-ia-search-{Guid.NewGuid():N}");

    public SearchToolsTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "class Alpha {\n  void Run() {}\n}\n");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "nothing here\n");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "c.cs"), "// run twice\nrun();\nrun();\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private static ToolInvocation Invoke(string toolId, string args) =>
        new(Guid.NewGuid(), toolId, args,
            new CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    [Fact]
    public async Task SearchText_FindsFileColonLine()
    {
        var tool = new SearchTextTool();
        var result = await tool.ExecuteAsync(Invoke(SearchTextTool.ToolId, Json(new
        {
            pattern = "run",
            directory = _dir,
            extensions = "cs",
            maxResults = 10,
        })), CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Contains("a.cs:2", result.Output);
        Assert.Contains("c.cs:2", result.Output);
        Assert.DoesNotContain("b.txt", result.Output);
    }

    [Fact]
    public async Task SearchText_NoMatches_ReportsCleanly()
    {
        var tool = new SearchTextTool();
        var result = await tool.ExecuteAsync(Invoke(SearchTextTool.ToolId, Json(new
        {
            pattern = "zzz_nope",
            directory = _dir,
        })), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains("No matches", result.Output);
    }

    [Fact]
    public async Task SearchText_MissingArgs_Fails()
    {
        var tool = new SearchTextTool();
        var result = await tool.ExecuteAsync(Invoke(SearchTextTool.ToolId, "{}"), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(FailureCategory.ToolFailure, result.FailureCategory);
    }

    [Fact]
    public async Task SearchFiles_FindsByName()
    {
        var tool = new SearchFilesTool();
        var result = await tool.ExecuteAsync(Invoke(SearchFilesTool.ToolId, Json(new
        {
            name = "c.cs",
            directory = _dir,
        })), CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Contains(Path.Combine("sub", "c.cs"), result.Output);
    }
}
