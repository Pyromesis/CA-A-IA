// CA-A-IA — Tests de WriteFile/EditFile/ExecuteCommand + permiso sobre workdir.

using CaAIA.Domain.Correlation;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Process;
using CaAIA.Domain.Tools;
using CaAIA.Infrastructure.Security;
using CaAIA.Infrastructure.Tools;

namespace CaAIA.Tests.Unit;

public sealed class WriteToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ca-a-ia-wt-{Guid.NewGuid():N}");
    public WriteToolsTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private static ToolInvocation Invoke(string toolId, string args) =>
        new(Guid.NewGuid(), toolId, args,
            new CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

    private static string Args(params (string Key, string Value)[] pairs) =>
        "{" + string.Join(",", pairs.Select(p =>
            $"\"{p.Key}\":{System.Text.Json.JsonSerializer.Serialize(p.Value)}")) + "}";

    [Fact]
    public async Task WriteThenRead_Roundtrips()
    {
        var path = Path.Combine(_dir, "a.txt");
        var write = await new WriteFileTool().ExecuteAsync(
            Invoke(WriteFileTool.ToolId, Args(("path", path), ("content", "hola"))), CancellationToken.None);
        Assert.True(write.Success, write.Error);
        Assert.Equal("hola", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Write_MissingParent_FailsCleanly()
    {
        var result = await new WriteFileTool().ExecuteAsync(
            Invoke(WriteFileTool.ToolId, Args(("path", Path.Combine(_dir, "nope", "a.txt")), ("content", "x"))),
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(FailureCategory.ToolFailure, result.FailureCategory);
    }

    [Fact]
    public async Task Edit_ReplacesSingleOccurrence()
    {
        var path = Path.Combine(_dir, "b.txt");
        await File.WriteAllTextAsync(path, "foo bar foo");
        var tool = new EditFileTool();

        var ambiguous = await tool.ExecuteAsync(
            Invoke(EditFileTool.ToolId, Args(("path", path), ("oldText", "foo"), ("newText", "baz"))),
            CancellationToken.None);
        Assert.False(ambiguous.Success); // 2 ocurrencias: se niega
        Assert.Equal("foo bar foo", await File.ReadAllTextAsync(path)); // intacto

        var ok = await tool.ExecuteAsync(
            Invoke(EditFileTool.ToolId, Args(("path", path), ("oldText", "bar"), ("newText", "BAR"))),
            CancellationToken.None);
        Assert.True(ok.Success, ok.Error);
        Assert.Equal("foo BAR foo", await File.ReadAllTextAsync(path));

        var missing = await tool.ExecuteAsync(
            Invoke(EditFileTool.ToolId, Args(("path", path), ("oldText", "zzz"), ("newText", "q"))),
            CancellationToken.None);
        Assert.False(missing.Success);
    }

    [Fact]
    public async Task Execute_ReturnsOutput_AndMapsExitCode()
    {
        var runner = new FakeRunner((0, "out-text", string.Empty));
        var tool = new ExecuteCommandTool(runner);
        var ok = await tool.ExecuteAsync(
            Invoke(ExecuteCommandTool.ToolId,
                $"{{\"command\":\"cmd\",\"args\":[],\"workdir\":{System.Text.Json.JsonSerializer.Serialize(_dir)}}}"),
            CancellationToken.None);
        Assert.True(ok.Success, ok.Error);
        Assert.Contains("out-text", ok.Output);
        Assert.Equal(("cmd", _dir), runner.Last);

        var failing = new ExecuteCommandTool(new FakeRunner((3, "out", "err")));
        var bad = await failing.ExecuteAsync(
            Invoke(ExecuteCommandTool.ToolId,
                $"{{\"command\":\"x\",\"workdir\":{System.Text.Json.JsonSerializer.Serialize(_dir)}}}"),
            CancellationToken.None);
        Assert.False(bad.Success);
        Assert.Equal(FailureCategory.ToolFailure, bad.FailureCategory);
        Assert.Contains("exit=3", bad.Output);
    }

    [Fact]
    public void PermissionService_WorkdirOutsideScope_IsDenied()
    {
        var service = new ToolPermissionService();
        var scope = new Domain.Security.ExecutionScope(
            new[] { _dir }, Array.Empty<string>(),
            ToolPermission.Read | ToolPermission.Execute,
            RequireConfirmationForWrite: true, RequireConfirmationForExecute: false);
        var definition = new ExecuteCommandTool(new FakeRunner((0, string.Empty, string.Empty))).Definition;

        var outside = Invoke(ExecuteCommandTool.ToolId,
            $"{{\"command\":\"cmd\",\"workdir\":{System.Text.Json.JsonSerializer.Serialize(Path.GetTempPath().TrimEnd('\\'))}}}");
        // C:\\Users\\...\\Temp está fuera de _dir → denegado (salvo que coincida; assert robusto abajo)
        var tempOutsideScope = !scope.IsPathAllowed(Path.GetTempPath().TrimEnd('\\'));
        Assert.True(tempOutsideScope);
        Assert.False(service.Authorize(outside, definition, scope).Allowed);

        var inside = Invoke(ExecuteCommandTool.ToolId,
            $"{{\"command\":\"cmd\",\"workdir\":{System.Text.Json.JsonSerializer.Serialize(_dir)}}}");
        Assert.True(service.Authorize(inside, definition, scope).Allowed);
    }

    private sealed class FakeRunner : ICommandRunner
    {
        private readonly (int Exit, string Out, string Err) _result;
        public (string Cmd, string Dir) Last;
        public FakeRunner((int, string, string) result) => _result = result;
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            string workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            Last = (fileName, workingDirectory);
            return Task.FromResult(new CommandResult(_result.Exit, _result.Out, _result.Err, TimeSpan.Zero));
        }
    }
}
