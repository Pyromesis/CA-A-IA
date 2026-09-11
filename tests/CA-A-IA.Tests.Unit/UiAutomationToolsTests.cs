// CA-A-IA — Tests de herramientas de autonomía (parseo, cotas, permisos).

using CaAIA.Application.Services;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Interaction;
using CaAIA.Domain.Tools;
using CaAIA.Infrastructure.Process;
using CaAIA.Infrastructure.Tools;

namespace CaAIA.Tests.Unit;

public sealed class UiAutomationToolsTests
{
    private sealed class FakeUi : IUiAutomation
    {
        public readonly List<string> Calls = new();
        public ScreenSize Screen { get; set; } = new(1920, 1080);
        public ScreenSize GetScreenSize() => Screen;
        public (int X, int Y) GetMousePosition() => (10, 20);
        public Task MoveMouseAsync(int x, int y, bool humanize, CancellationToken ct)
        {
            Calls.Add($"move:{x},{y},{humanize}");
            return Task.CompletedTask;
        }

        public Task ClickAsync(string button, bool doubleClick, CancellationToken ct)
        {
            Calls.Add($"click:{button},{doubleClick}");
            return Task.CompletedTask;
        }

        public Task ScrollAsync(int dx, int dy, CancellationToken ct)
        {
            Calls.Add($"scroll:{dx},{dy}");
            return Task.CompletedTask;
        }

        public Task TypeTextAsync(string text, CancellationToken ct)
        {
            Calls.Add($"type:{text.Length}");
            return Task.CompletedTask;
        }

        public Task PressKeyAsync(string key, IReadOnlyList<string> modifiers, CancellationToken ct)
        {
            Calls.Add($"key:{key}+{string.Join("+", modifiers)}");
            return Task.CompletedTask;
        }

        public Task<ActiveWindow> OpenAppAsync(string name, CancellationToken ct)
        {
            Calls.Add($"open:{name}");
            return Task.FromResult(new ActiveWindow("brave", "Brave"));
        }

        public Task OpenUrlAsync(string url, string? browser, CancellationToken ct)
        {
            Calls.Add($"url:{url},{browser}");
            return Task.CompletedTask;
        }

        public ActiveWindow GetActiveWindow() => new("brave", "YouTube — Brave");

        public Task<bool> WaitForActiveWindowAsync(string text, int timeoutSeconds, CancellationToken ct)
        {
            Calls.Add($"wait:{text}");
            return Task.FromResult(true);
        }

        public Task<string> CaptureScreenshotAsync(string directory, CancellationToken ct)
        {
            Calls.Add($"shot:{directory}");
            return Task.FromResult(@"C:\tmp\shot-1.png");
        }
    }

    private static ToolInvocation Invoke(string toolId, string args) =>
        new(Guid.NewGuid(), toolId, args,
            new Domain.Correlation.CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public void UiTools_RequireProcessControl_ForMutations()
    {
        ITool[] mutating =
        {
            new MoveMouseTool(new FakeUi()), new ClickMouseTool(new FakeUi()),
            new ScrollMouseTool(new FakeUi()), new TypeTextTool(new FakeUi()),
            new PressKeyTool(new FakeUi()), new OpenAppTool(new FakeUi()),
            new OpenUrlTool(new FakeUi()),
        };
        Assert.All(mutating, t =>
        {
            Assert.Equal(ToolKind.UiAutomation, t.Definition.Kind);
            Assert.True(t.Definition.RequiredPermissions.HasFlag(ToolPermission.ProcessControl));
        });
    }

    [Fact]
    public void UiTools_Observational_RequireOnlyRead()
    {
        ITool[] observational =
        {
            new GetScreenSizeTool(new FakeUi()), new GetActiveWindowTool(new FakeUi()),
            new WaitForActiveWindowTool(new FakeUi()), new ScreenshotTool(new FakeUi()),
        };
        Assert.All(observational, t =>
        {
            Assert.Equal(ToolKind.UiAutomation, t.Definition.Kind);
            Assert.Equal(ToolPermission.Read, t.Definition.RequiredPermissions);
        });
    }

    [Fact]
    public async Task MoveMouse_ParsesCoords()
    {
        var ui = new FakeUi();
        var result = await new MoveMouseTool(ui).ExecuteAsync(
            Invoke(MoveMouseTool.ToolId, """{"x":100,"y":200}"""), CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Contains("move:100,200,True", ui.Calls);
        var missing = await new MoveMouseTool(ui).ExecuteAsync(
            Invoke(MoveMouseTool.ToolId, "{}"), CancellationToken.None);
        Assert.False(missing.Success);
    }

    [Fact]
    public async Task Click_ValidatesButton_AndMovesFirst()
    {
        var ui = new FakeUi();
        var result = await new ClickMouseTool(ui).ExecuteAsync(
            Invoke(ClickMouseTool.ToolId, """{"button":"right","double":true,"x":5,"y":6}"""),
            CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { "move:5,6,True", "click:right,True" }, ui.Calls);
        var bad = await new ClickMouseTool(ui).ExecuteAsync(
            Invoke(ClickMouseTool.ToolId, """{"button":"side"}"""), CancellationToken.None);
        Assert.False(bad.Success);
    }

    [Fact]
    public async Task TypeText_RequiresText()
    {
        var ui = new FakeUi();
        var ok = await new TypeTextTool(ui).ExecuteAsync(
            Invoke(TypeTextTool.ToolId, """{"text":"hola"}"""), CancellationToken.None);
        Assert.True(ok.Success, ok.Error);
        var empty = await new TypeTextTool(ui).ExecuteAsync(
            Invoke(TypeTextTool.ToolId, "{}"), CancellationToken.None);
        Assert.False(empty.Success);
    }

    [Fact]
    public async Task PressKey_ParsesModifiers()
    {
        var ui = new FakeUi();
        var ok = await new PressKeyTool(ui).ExecuteAsync(
            Invoke(PressKeyTool.ToolId, """{"key":"Enter","modifiers":["ctrl"]}"""),
            CancellationToken.None);
        Assert.True(ok.Success, ok.Error);
        Assert.Contains("key:Enter+ctrl", ui.Calls);
        var unknown = await new PressKeyTool(ui).ExecuteAsync(
            Invoke(PressKeyTool.ToolId, """{"key":"HyperSuper"}"""), CancellationToken.None);
        // La validación real la hace UiAutomation.ResolveKey; el fake acepta todo.
        Assert.True(unknown.Success);
    }

    [Fact]
    public async Task GetScreenSize_ReturnsBounds()
    {
        var result = await new GetScreenSizeTool(new FakeUi()).ExecuteAsync(
            Invoke(GetScreenSizeTool.ToolId, "{}"), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("1920x1080", result.Output);
    }

    [Fact]
    public async Task OpenApp_OpensAndReports()
    {
        var ui = new FakeUi();
        var ok = await new OpenAppTool(ui).ExecuteAsync(
            Invoke(OpenAppTool.ToolId, """{"name":"brave"}"""), CancellationToken.None);
        Assert.True(ok.Success, ok.Error);
        Assert.Contains("brave", ok.Output, StringComparison.OrdinalIgnoreCase);
        var missing = await new OpenAppTool(ui).ExecuteAsync(
            Invoke(OpenAppTool.ToolId, "{}"), CancellationToken.None);
        Assert.False(missing.Success);
    }

    [Fact]
    public async Task OpenUrl_RequiresHttp()
    {
        var ui = new FakeUi();
        var ok = await new OpenUrlTool(ui).ExecuteAsync(
            Invoke(OpenUrlTool.ToolId, """{"url":"https://youtube.com"}"""), CancellationToken.None);
        Assert.True(ok.Success, ok.Error);
    }

    [Fact]
    public async Task ActiveWindow_ReportsForeground()
    {
        var result = await new GetActiveWindowTool(new FakeUi()).ExecuteAsync(
            Invoke(GetActiveWindowTool.ToolId, "{}"), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains("YouTube", result.Output);
    }

    [Fact]
    public async Task WaitWindow_Waits()
    {
        var ui = new FakeUi();
        var result = await new WaitForActiveWindowTool(ui).ExecuteAsync(
            Invoke(WaitForActiveWindowTool.ToolId, """{"text":"youtube"}"""), CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Contains("wait:youtube", ui.Calls);
    }

    [Theory]
    [InlineData("UiOpenApp", "{\"name\":\"x\"}", "▶ Abrió x")]
    [InlineData("UiOpenUrl", "{\"url\":\"https://youtube.com/abc\"}", "▶ Abrió https://youtube.com/abc")]
    public void ForToolCompleted_NarratesUiActions(string toolId, string args, string expected) =>
        Assert.Equal(expected, AgentActivityText.ForToolCompleted(toolId, args, true, null));

    [Fact]
    public async Task Screenshot_ReturnsPath_AsAttachment()
    {
        var ui = new FakeUi();
        var tool = new ScreenshotTool(ui);
        var result = await tool.ExecuteAsync(
            Invoke(ScreenshotTool.ToolId, "{}"), CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Equal(@"C:\tmp\shot-1.png", result.AttachmentPath);
        Assert.Contains(result.AttachmentPath, result.Output);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    public void EaseInOutCubic_HitsKeyframes(double t, double expected)
    {
        Assert.Equal(expected, UiAutomation.EaseInOutCubic(t), precision: 9);
    }

    [Fact]
    public void EaseInOutCubic_IsMonotonic()
    {
        var prev = -1.0;
        for (var i = 0; i <= 20; i++)
        {
            var v = UiAutomation.EaseInOutCubic(i / 20.0);
            Assert.True(v >= prev);
            prev = v;
        }
    }

    [Theory]
    [InlineData("Enter", true)]
    [InlineData("win", true)]
    [InlineData("F5", true)]
    [InlineData("a", true)]
    [InlineData("HyperSuper", false)]
    [InlineData("", false)]
    public void ResolveKey_MapsKnownKeys(string key, bool expected)
    {
        Assert.Equal(expected, UiAutomation.ResolveKey(key).HasValue);
    }
}
