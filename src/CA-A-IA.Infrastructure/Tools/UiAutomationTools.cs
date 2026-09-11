// CA-A-IA — Herramientas de autonomía: ratón y teclado reales como un humano.
// Argumentos JSON validados y acotados; la autorización (Nivel 1 confirma cada
// acción) la aplica LlmTaskExecutor antes de llegar aquí.

using System.Diagnostics;
using System.Text.Json;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Interaction;
using CaAIA.Domain.Tools;

namespace CaAIA.Infrastructure.Tools;

/// <summary>Resolución del monitor principal. Sin argumentos.</summary>
public sealed class GetScreenSizeTool : ITool
{
    public const string ToolId = "UiGetScreen";
    public ToolDefinition Definition { get; } = new(
        ToolId, "UiGetScreen", "Gets the primary monitor size (bounds for mouse coordinates). No arguments.",
        ToolKind.UiAutomation, ToolPermission.ProcessControl,
        Array.Empty<ToolParameter>(), TimeSpan.FromSeconds(10));

    private readonly IUiAutomation _ui;
    public GetScreenSizeTool(IUiAutomation ui) => _ui = ui;

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var size = _ui.GetScreenSize();
            return Task.FromResult(WriteFileTool.Ok(invocation, $"{size.Width}x{size.Height}", sw));
        }
        catch (Exception ex)
        {
            return Task.FromResult(WriteFileTool.Fail(invocation, ex.Message, sw));
        }
    }
}

/// <summary>Mueve el ratón (humanizado por defecto). Argumentos: { "x", "y", "humanize"? }.</summary>
public sealed class MoveMouseTool : ITool
{
    public const string ToolId = "UiMoveMouse";
    public ToolDefinition Definition { get; } = new(
        ToolId, "UiMoveMouse", "Moves the mouse to screen coordinates, human-like unless humanize:false.",
        ToolKind.UiAutomation, ToolPermission.ProcessControl,
        new[]
        {
            new ToolParameter("x", "X coordinate.", "integer", IsRequired: true),
            new ToolParameter("y", "Y coordinate.", "integer", IsRequired: true),
            new ToolParameter("humanize", "Human-like motion (default true).", "boolean", IsRequired: false, DefaultJson: "true"),
        },
        TimeSpan.FromSeconds(30));

    private readonly IUiAutomation _ui;
    public MoveMouseTool(IUiAutomation ui) => _ui = ui;

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            if (!TryInt(doc, "x", out var x) || !TryInt(doc, "y", out var y))
            {
                return WriteFileTool.Fail(invocation, "Missing required integer arguments 'x'/'y'.", sw);
            }

            var humanize = !doc.RootElement.TryGetProperty("humanize", out var h)
                || h.ValueKind != JsonValueKind.False;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(EffectiveTimeout(invocation));
            await _ui.MoveMouseAsync(x, y, humanize, timeout.Token).ConfigureAwait(false);
            var pos = _ui.GetMousePosition();
            return WriteFileTool.Ok(invocation, $"Mouse at {pos.X},{pos.Y}.", sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WriteFileTool.Fail(invocation, "UiMoveMouse timed out.", sw);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return WriteFileTool.Fail(invocation, ex.Message, sw);
        }
    }

    internal static bool TryInt(JsonDocument doc, string name, out int value)
    {
        value = 0;
        return doc.RootElement.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
    }

    internal static TimeSpan EffectiveTimeout(ToolInvocation invocation) =>
        invocation.TimeoutOverride ?? TimeSpan.FromSeconds(30);
}

/// <summary>Clic donde esté el ratón (o mueve primero si se dan x/y). { "button"?, "double"?, "x"?, "y"? }.</summary>
public sealed class ClickMouseTool : ITool
{
    public const string ToolId = "UiClick";
    public ToolDefinition Definition { get; } = new(
        ToolId, "UiClick", "Clicks at the cursor (or moves to x/y first, human-like). Buttons: left/right/middle.",
        ToolKind.UiAutomation, ToolPermission.ProcessControl,
        new[]
        {
            new ToolParameter("button", "left|right|middle (default left).", "string", IsRequired: false, DefaultJson: "\"left\""),
            new ToolParameter("double", "Double click (default false).", "boolean", IsRequired: false, DefaultJson: "false"),
            new ToolParameter("x", "Optional X to move to first.", "integer", IsRequired: false),
            new ToolParameter("y", "Optional Y to move to first.", "integer", IsRequired: false),
        },
        TimeSpan.FromSeconds(30));

    private readonly IUiAutomation _ui;
    public ClickMouseTool(IUiAutomation ui) => _ui = ui;

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var button = doc.RootElement.TryGetProperty("button", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() ?? "left" : "left";
            if (button is not ("left" or "right" or "middle"))
            {
                return WriteFileTool.Fail(invocation, "button must be left|right|middle.", sw);
            }

            var dbl = doc.RootElement.TryGetProperty("double", out var d) && d.ValueKind == JsonValueKind.True;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(MoveMouseTool.EffectiveTimeout(invocation));
            if (MoveMouseTool.TryInt(doc, "x", out var x) && MoveMouseTool.TryInt(doc, "y", out var y))
            {
                await _ui.MoveMouseAsync(x, y, humanize: true, timeout.Token).ConfigureAwait(false);
            }

            await _ui.ClickAsync(button, dbl, timeout.Token).ConfigureAwait(false);
            var pos = _ui.GetMousePosition();
            return WriteFileTool.Ok(invocation,
                $"{(dbl ? "Double-clicked" : "Clicked")} {button} at {pos.X},{pos.Y}.", sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WriteFileTool.Fail(invocation, "UiClick timed out.", sw);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return WriteFileTool.Fail(invocation, ex.Message, sw);
        }
    }
}

/// <summary>Rueda del ratón. { "dx"?, "dy"? } en clics (±20).</summary>
public sealed class ScrollMouseTool : ITool
{
    public const string ToolId = "UiScroll";
    public ToolDefinition Definition { get; } = new(
        ToolId, "UiScroll", "Scrolls the mouse wheel (clicks, clamped to ±20).",
        ToolKind.UiAutomation, ToolPermission.ProcessControl,
        new[]
        {
            new ToolParameter("dx", "Horizontal clicks.", "integer", IsRequired: false, DefaultJson: "0"),
            new ToolParameter("dy", "Vertical clicks.", "integer", IsRequired: false, DefaultJson: "0"),
        },
        TimeSpan.FromSeconds(10));

    private readonly IUiAutomation _ui;
    public ScrollMouseTool(IUiAutomation ui) => _ui = ui;

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            MoveMouseTool.TryInt(doc, "dx", out var dx);
            MoveMouseTool.TryInt(doc, "dy", out var dy);
            await _ui.ScrollAsync(dx, dy, cancellationToken).ConfigureAwait(false);
            return WriteFileTool.Ok(invocation, $"Scrolled ({dx},{dy}).", sw);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return WriteFileTool.Fail(invocation, ex.Message, sw);
        }
    }
}

/// <summary>Escribe texto como un humano (ritmo irregular). { "text" } (máx 2000).</summary>
public sealed class TypeTextTool : ITool
{
    public const string ToolId = "UiTypeText";
    public ToolDefinition Definition { get; } = new(
        ToolId, "UiTypeText", "Types text with human rhythm at the focused control.",
        ToolKind.UiAutomation, ToolPermission.ProcessControl,
        new[] { new ToolParameter("text", "Text to type (max 2000 chars).", "string", IsRequired: true) },
        TimeSpan.FromSeconds(120));

    private readonly IUiAutomation _ui;
    public TypeTextTool(IUiAutomation ui) => _ui = ui;

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var text = WriteFileTool.Required(doc, "text");
            if (string.IsNullOrEmpty(text))
            {
                return WriteFileTool.Fail(invocation, "Missing required argument 'text'.", sw);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(invocation.TimeoutOverride ?? Definition.DefaultTimeout);
            await _ui.TypeTextAsync(text[..Math.Min(text.Length, 2000)], timeout.Token).ConfigureAwait(false);
            return WriteFileTool.Ok(invocation, $"Typed {Math.Min(text.Length, 2000)} chars.", sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WriteFileTool.Fail(invocation, "UiTypeText timed out.", sw);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return WriteFileTool.Fail(invocation, ex.Message, sw);
        }
    }
}

/// <summary>Abre una app por nombre (o la trae al frente si ya corre). { "name" }.</summary>
public sealed class OpenAppTool : ITool
{
    public const string ToolId = "UiOpenApp";
    public ToolDefinition Definition { get; } = new(
        ToolId, "UiOpenApp", "Opens an app by name (brave, notepad…); focuses it if already running instead of duplicating.",
        ToolKind.UiAutomation, ToolPermission.ProcessControl,
        new[] { new ToolParameter("name", "App name.", "string", IsRequired: true) },
        TimeSpan.FromSeconds(30));

    private readonly IUiAutomation _ui;
    public OpenAppTool(IUiAutomation ui) => _ui = ui;

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var name = WriteFileTool.Required(doc, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return WriteFileTool.Fail(invocation, "Missing required argument 'name'.", sw);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(invocation.TimeoutOverride ?? Definition.DefaultTimeout);
            var active = await _ui.OpenAppAsync(name, timeout.Token).ConfigureAwait(false);
            var detail = string.IsNullOrWhiteSpace(active.Title)
                ? active.ProcessName : $"{active.ProcessName} — {active.Title}";
            return WriteFileTool.Ok(invocation, $"Open: {detail}.", sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WriteFileTool.Fail(invocation, "UiOpenApp timed out.", sw);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return WriteFileTool.Fail(invocation, ex.Message, sw);
        }
    }
}

/// <summary>Abre una URL (navegador por defecto o indicado). { "url", "browser"? }.</summary>
public sealed class OpenUrlTool : ITool
{
    public const string ToolId = "UiOpenUrl";
    public ToolDefinition Definition { get; } = new(
        ToolId, "UiOpenUrl", "Opens an http(s) URL in the default (or given) browser.",
        ToolKind.UiAutomation, ToolPermission.ProcessControl,
        new[]
        {
            new ToolParameter("url", "Absolute http(s) URL.", "string", IsRequired: true),
            new ToolParameter("browser", "brave|chrome|edge|firefox… (default browser if empty).", "string", IsRequired: false),
        },
        TimeSpan.FromSeconds(30));

    private readonly IUiAutomation _ui;
    public OpenUrlTool(IUiAutomation ui) => _ui = ui;

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var url = WriteFileTool.Required(doc, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                return WriteFileTool.Fail(invocation, "Missing required argument 'url'.", sw);
            }

            var browser = doc.RootElement.TryGetProperty("browser", out var b)
                && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            await _ui.OpenUrlAsync(url, browser, cancellationToken).ConfigureAwait(false);
            return WriteFileTool.Ok(invocation, $"Opened {url}.", sw);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return WriteFileTool.Fail(invocation, ex.Message, sw);
        }
    }
}

/// <summary>Ventana en primer plano (proceso + título). Sin argumentos.</summary>
public sealed class GetActiveWindowTool : ITool
{
    public const string ToolId = "UiActiveWindow";
    public ToolDefinition Definition { get; } = new(
        ToolId, "UiActiveWindow", "Gets the foreground window (process + title) to verify what is open.",
        ToolKind.UiAutomation, ToolPermission.ProcessControl,
        Array.Empty<ToolParameter>(), TimeSpan.FromSeconds(10));

    private readonly IUiAutomation _ui;
    public GetActiveWindowTool(IUiAutomation ui) => _ui = ui;

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var active = _ui.GetActiveWindow();
            var detail = string.IsNullOrWhiteSpace(active.Title)
                ? active.ProcessName : $"{active.ProcessName} — {active.Title}";
            return Task.FromResult(string.IsNullOrWhiteSpace(detail)
                ? WriteFileTool.Fail(invocation, "No foreground window.", sw)
                : WriteFileTool.Ok(invocation, detail, sw));
        }
        catch (Exception ex)
        {
            return Task.FromResult(WriteFileTool.Fail(invocation, ex.Message, sw));
        }
    }
}

/// <summary>Espera a que el primer plano contenga un texto. { "text", "timeoutSeconds"? }.</summary>
public sealed class WaitForActiveWindowTool : ITool
{
    public const string ToolId = "UiWaitWindow";
    public ToolDefinition Definition { get; } = new(
        ToolId, "UiWaitWindow", "Waits until the foreground window contains text (process or title) instead of guessing blindly.",
        ToolKind.UiAutomation, ToolPermission.ProcessControl,
        new[]
        {
            new ToolParameter("text", "Text to wait for.", "string", IsRequired: true),
            new ToolParameter("timeoutSeconds", "Timeout 3-60 (default 15).", "integer", IsRequired: false, DefaultJson: "15"),
        },
        TimeSpan.FromSeconds(70));

    private readonly IUiAutomation _ui;
    public WaitForActiveWindowTool(IUiAutomation ui) => _ui = ui;

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var text = WriteFileTool.Required(doc, "text");
            if (string.IsNullOrWhiteSpace(text))
            {
                return WriteFileTool.Fail(invocation, "Missing required argument 'text'.", sw);
            }

            var timeout = doc.RootElement.TryGetProperty("timeoutSeconds", out var t) && t.TryGetInt32(out var n)
                ? n : 15;
            var found = await _ui.WaitForActiveWindowAsync(text, timeout, cancellationToken).ConfigureAwait(false);
            if (!found)
            {
                var now = _ui.GetActiveWindow();
                return WriteFileTool.Fail(invocation,
                    $"Timed out waiting for '{text}'. Foreground now: {now.ProcessName} — {now.Title}.", sw);
            }

            var active = _ui.GetActiveWindow();
            return WriteFileTool.Ok(invocation, $"Foreground: {active.ProcessName} — {active.Title}.", sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WriteFileTool.Fail(invocation, "UiWaitWindow timed out.", sw);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return WriteFileTool.Fail(invocation, ex.Message, sw);
        }
    }
}
/// <summary>Pulsa una tecla (Enter, Tab, Win, F5, letras…) + modificadores. { "key", "modifiers"? }.</summary>
public sealed class PressKeyTool : ITool
{
    public const string ToolId = "UiPressKey";
    public ToolDefinition Definition { get; } = new(
        ToolId, "UiPressKey", "Presses a key, optionally holding modifiers (ctrl/shift/alt/win).",
        ToolKind.UiAutomation, ToolPermission.ProcessControl,
        new[]
        {
            new ToolParameter("key", "Enter, Tab, Escape, Win, arrows, F1-F12, letters…", "string", IsRequired: true),
            new ToolParameter("modifiers", "E.g. [\"ctrl\"].", "array", IsRequired: false, DefaultJson: "[]"),
        },
        TimeSpan.FromSeconds(30));

    private readonly IUiAutomation _ui;
    public PressKeyTool(IUiAutomation ui) => _ui = ui;

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var key = WriteFileTool.Required(doc, "key");
            if (string.IsNullOrWhiteSpace(key))
            {
                return WriteFileTool.Fail(invocation, "Missing required argument 'key'.", sw);
            }

            var modifiers = new List<string>();
            if (doc.RootElement.TryGetProperty("modifiers", out var m) && m.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in m.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        modifiers.Add(item.GetString()!);
                    }
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(MoveMouseTool.EffectiveTimeout(invocation));
            await _ui.PressKeyAsync(key, modifiers, timeout.Token).ConfigureAwait(false);
            return WriteFileTool.Ok(invocation,
                modifiers.Count == 0 ? $"Pressed {key}." : $"Pressed {string.Join("+", modifiers)}+{key}.", sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WriteFileTool.Fail(invocation, "UiPressKey timed out.", sw);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return WriteFileTool.Fail(invocation, ex.Message, sw);
        }
    }
}
