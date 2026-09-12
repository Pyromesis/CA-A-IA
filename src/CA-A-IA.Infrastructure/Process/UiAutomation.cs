// CA-A-IA — Ratón y teclado reales vía SendInput, con movimiento HUMANIZADO:
// curva ease-in-out (acelera/frena como una mano), jitter, velocidad variable y
// pausas: nada de saltos instantáneos de robot.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CaAIA.Domain.Interaction;

namespace CaAIA.Infrastructure.Process;

/// <summary>
/// <see cref="IUiAutomation"/> con Win32 SendInput. Monitor principal; toda
/// coordenada se recorta a la pantalla. Los delays observan el token.
/// </summary>
[SupportedOSPlatform("windows6.1")]
public sealed class UiAutomation : IUiAutomation
{
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private const int MOUSEEVENTF_MOVE = 0x0001;
    private const int MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const int MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const int MOUSEEVENTF_LEFTUP = 0x0004;
    private const int MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const int MOUSEEVENTF_RIGHTUP = 0x0010;
    private const int MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const int MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const int MOUSEEVENTF_WHEEL = 0x0800;
    private const int MOUSEEVENTF_HWHEEL = 0x1000;

    private const int KEYEVENTF_KEYUP = 0x0002;
    private const int KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public int dwFlags;
        public int time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    private const int SW_RESTORE = 9;

    public ScreenSize GetScreenSize() =>
        new(Math.Max(1, GetSystemMetrics(SM_CXSCREEN)), Math.Max(1, GetSystemMetrics(SM_CYSCREEN)));

    public (int X, int Y) GetMousePosition() =>
        GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    public async Task MoveMouseAsync(int x, int y, bool humanize, CancellationToken ct)
    {
        var screen = GetScreenSize();
        var target = (X: Math.Clamp(x, 0, screen.Width - 1), Y: Math.Clamp(y, 0, screen.Height - 1));
        var from = GetMousePosition();
        if (!humanize)
        {
            SendAbsolute(target.X, target.Y, screen);
            return;
        }

        // Mano humana: ease-in-out + jitter + velocidad variable por tramo.
        var dx = target.X - from.X;
        var dy = target.Y - from.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < 2)
        {
            return;
        }

        var steps = (int)Math.Clamp(distance / 10, 10, 100);
        for (var i = 1; i <= steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = EaseInOutCubic((double)i / steps);
            var px = from.X + (target.X - from.X) * t;
            var py = from.Y + (target.Y - from.Y) * t;
            if (i < steps)
            {
                // Jitter de mano (nunca en el punto final: clic exacto).
                px += Random.Shared.Next(-2, 3);
                py += Random.Shared.Next(-2, 3);
            }

            SendAbsolute((int)Math.Round(px), (int)Math.Round(py), screen);
            await Task.Delay(Random.Shared.Next(6, 13), ct).ConfigureAwait(false);
        }

        // Micro-pausa de "dwell" antes de actuar, como un humano que apunta.
        await Task.Delay(Random.Shared.Next(40, 121), ct).ConfigureAwait(false);
    }

    /// <summary>Curva suave: arranca y frena despacio, rápido en medio.</summary>
    internal static double EaseInOutCubic(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    public async Task ClickAsync(string button, bool doubleClick, CancellationToken ct)
    {
        var (down, up) = (button ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };

        var clicks = doubleClick ? 2 : 1;
        for (var i = 0; i < clicks; i++)
        {
            ct.ThrowIfCancellationRequested();
            SendMouse(down, 0);
            await Task.Delay(Random.Shared.Next(30, 81), ct).ConfigureAwait(false);
            SendMouse(up, 0);
            if (i + 1 < clicks)
            {
                await Task.Delay(Random.Shared.Next(50, 111), ct).ConfigureAwait(false);
            }
        }
    }

    public Task ScrollAsync(int deltaX, int deltaY, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dx = Math.Clamp(deltaX, -20, 20);
        var dy = Math.Clamp(deltaY, -20, 20);
        if (dy != 0)
        {
            SendMouse(MOUSEEVENTF_WHEEL, dy * 120);
        }

        if (dx != 0)
        {
            SendMouse(MOUSEEVENTF_HWHEEL, dx * 120);
        }

        return Task.CompletedTask;
    }

    public async Task TypeTextAsync(string text, CancellationToken ct)
    {
        // Ritmo humano ágil: 15–60 ms por tecla + pausas de "pensar" ocasionales.
        var count = 0;
        foreach (var ch in text ?? string.Empty)
        {
            ct.ThrowIfCancellationRequested();
            if (count >= 2000)
            {
                break;
            }

            SendUnicode(ch, keyUp: false);
            await Task.Delay(Random.Shared.Next(10, 31), ct).ConfigureAwait(false);
            SendUnicode(ch, keyUp: true);
            count++;
            await Task.Delay(Random.Shared.Next(15, 61)
                + (Random.Shared.Next(100) < 5 ? Random.Shared.Next(200, 451) : 0), ct)
                .ConfigureAwait(false);
        }
    }

    public async Task PressKeyAsync(string key, IReadOnlyList<string> modifiers, CancellationToken ct)
    {
        var vk = ResolveKey(key);
        if (vk is null)
        {
            throw new ArgumentException($"Unknown key '{key}'.", nameof(key));
        }

        var held = new List<ushort>();
        try
        {
            foreach (var modifier in modifiers ?? Array.Empty<string>())
            {
                ct.ThrowIfCancellationRequested();
                var mod = ResolveKey(modifier);
                if (mod.HasValue && !held.Contains(mod.Value))
                {
                    SendKey(mod.Value, keyUp: false);
                    held.Add(mod.Value);
                    await Task.Delay(Random.Shared.Next(30, 80), ct).ConfigureAwait(false);
                }
            }

            SendKey(vk.Value, keyUp: false);
            await Task.Delay(Random.Shared.Next(40, 110), ct).ConfigureAwait(false);
            SendKey(vk.Value, keyUp: true);
        }
        finally
        {
            held.Reverse();
            foreach (var mod in held)
            {
                try
                {
                    SendKey(mod, keyUp: true);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    internal static ushort? ResolveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var name = key.Trim().ToLowerInvariant();
        if (name.Length == 1)
        {
            // Carácter imprimible: VkKeyScan elige VK + shift solo.
            var scan = VkKeyScan(name[0]);
            if ((scan & 0xFF00) == 0xFFFF)
            {
                return null;
            }

            return (ushort)(scan & 0xFF);
        }

        return name switch
        {
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "escape" or "esc" => 0x1B,
            "backspace" => 0x08,
            "space" => 0x20,
            "up" or "arrowup" => 0x26,
            "down" or "arrowdown" => 0x28,
            "left" or "arrowleft" => 0x25,
            "right" or "arrowright" => 0x27,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "insert" => 0x2D,
            "delete" or "del" => 0x2E,
            "shift" => 0x10,
            "ctrl" or "control" => 0x11,
            "alt" => 0x12,
            "win" or "windows" or "meta" => 0x5B,
            "capslock" => 0x14,
            "numlock" => 0x90,
            "printscreen" => 0x2C,
            "pause" => 0x13,
            "f1" => 0x70,
            "f2" => 0x71,
            "f3" => 0x72,
            "f4" => 0x73,
            "f5" => 0x74,
            "f6" => 0x75,
            "f7" => 0x76,
            "f8" => 0x77,
            "f9" => 0x78,
            "f10" => 0x79,
            "f11" => 0x7A,
            "f12" => 0x7B,
            _ => null,
        };
    }

    private static void SendAbsolute(int x, int y, ScreenSize screen)
    {
        var input = new INPUT
        {
            type = 0,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = x * 65535 / Math.Max(1, screen.Width - 1),
                    dy = y * 65535 / Math.Max(1, screen.Height - 1),
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                },
            },
        };
        SendChecked(new[] { input });
    }

    private static void SendMouse(int flags, int data)
    {
        var input = new INPUT
        {
            type = 0,
            U = new InputUnion { mi = new MOUSEINPUT { mouseData = data, dwFlags = flags } },
        };
        SendChecked(new[] { input });
    }

    private static void SendUnicode(char ch, bool keyUp)
    {
        var input = new INPUT
        {
            type = 1,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wScan = ch,
                    dwFlags = KEYEVENTF_UNICODE | (keyUp ? (uint)KEYEVENTF_KEYUP : 0),
                },
            },
        };
        SendChecked(new[] { input });
    }

    private static void SendKey(ushort vk, bool keyUp)
    {
        var input = new INPUT
        {
            type = 1,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = vk, dwFlags = keyUp ? (uint)KEYEVENTF_KEYUP : 0 },
            },
        };
        SendChecked(new[] { input });
    }

    private static void SendChecked(INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == 0)
        {
            throw new InvalidOperationException("SendInput was blocked (UIPI?) or failed.");
        }
    }

    /// <summary>Máximo de ancho de captura: suficiente para leer, barato de enviar.</summary>
    internal const int MaxScreenshotWidth = 1280;

    public Task<string> CaptureScreenshotAsync(string directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Directory is required.", nameof(directory));
        }

        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            var screen = GetScreenSize();
            using var full = new System.Drawing.Bitmap(screen.Width, screen.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(full))
            {
                g.CopyFromScreen(0, 0, 0, 0, full.Size,
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }

            var width = Math.Min(MaxScreenshotWidth, full.Width);
            var height = Math.Max(1, full.Height * width / Math.Max(1, full.Width));
            using var small = new System.Drawing.Bitmap(full, new System.Drawing.Size(width, height));
            Directory.CreateDirectory(directory);
            var stamp = DateTimeOffset.UtcNow.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
            var path = Path.Combine(directory, $"shot-{stamp}.png");
            small.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }, ct);
    }

    public async Task<ActiveWindow> OpenAppAsync(string name, CancellationToken ct)
    {
        var token = NormalizeAppName(name);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("App name is required.", nameof(name));
        }

        // ¿Ya está abierta? Traerla al frente en vez de duplicarla.
        var running = FindProcess(token);
        if (running is not null)
        {
            var focused = FocusWindow(running);
            running.Dispose();
            return focused;
        }

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LaunchToken(token),
                UseShellExecute = true,
            }) ?? throw new InvalidOperationException($"Could not launch '{name}'.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not launch '{name}': {ex.Message}", ex);
        }

        using (process)
        {
            // Esperar ventana principal (hasta 10 s): sin ventana no hay nada que ver.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    process.Refresh();
                    if (process.HasExited)
                    {
                        break;
                    }

                    if (process.MainWindowHandle != nint.Zero)
                    {
                        return FocusWindow(process);
                    }
                }
                catch (Exception)
                {
                    break;
                }

                await Task.Delay(400, ct).ConfigureAwait(false);
            }

            try
            {
                return new ActiveWindow(process.ProcessName, process.MainWindowTitle);
            }
            catch (Exception)
            {
                return new ActiveWindow(token, string.Empty);
            }
        }
    }

    public Task OpenUrlAsync(string url, string? browser, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("URL must be absolute http(s).", nameof(url));
        }

        try
        {
            if (string.IsNullOrWhiteSpace(browser))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true,
                });
            }
            else
            {
                var exe = NormalizeAppName(browser);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LaunchToken(exe), url)
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not open URL: {ex.Message}", ex);
        }

        return Task.CompletedTask;
    }

    public ActiveWindow GetActiveWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero)
        {
            return new ActiveWindow(string.Empty, string.Empty);
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        string processName;
        string title = WindowTitle(hwnd);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            processName = process.ProcessName;
            if (string.IsNullOrWhiteSpace(title))
            {
                try
                {
                    title = process.MainWindowTitle;
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
            processName = string.Empty;
        }

        return new ActiveWindow(processName, title);
    }

    public async Task<bool> WaitForActiveWindowAsync(string text, int timeoutSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text is required.", nameof(text));
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds <= 0 ? 15 : timeoutSeconds, 3, 60));
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var active = GetActiveWindow();
            if (active.ProcessName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || active.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return false;
    }

    private static System.Diagnostics.Process? FindProcess(string token)
    {
        try
        {
            return System.Diagnostics.Process.GetProcesses()
                .FirstOrDefault(p =>
                {
                    string name;
                    try
                    {
                        name = p.ProcessName;
                    }
                    catch (Exception)
                    {
                        p.Dispose();
                        return false;
                    }

                    if (!name.Equals(token, StringComparison.OrdinalIgnoreCase)
                        && !name.Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Dispose();
                        return false;
                    }

                    return true;
                });
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ActiveWindow FocusWindow(System.Diagnostics.Process process)
    {
        try
        {
            var hwnd = process.MainWindowHandle;
            if (hwnd == nint.Zero)
            {
                return new ActiveWindow(process.ProcessName, string.Empty);
            }

            try
            {
                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SW_RESTORE);
                }
            }
            catch (Exception)
            {
            }

            try
            {
                SetForegroundWindow(hwnd);
            }
            catch (Exception)
            {
            }

            return new ActiveWindow(process.ProcessName, process.MainWindowTitle);
        }
        catch (Exception)
        {
            try
            {
                return new ActiveWindow(process.ProcessName, string.Empty);
            }
            catch (Exception)
            {
                return new ActiveWindow(string.Empty, string.Empty);
            }
        }
    }

    private static string WindowTitle(nint hwnd)
    {
        try
        {
            var sb = new System.Text.StringBuilder(512);
            return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string NormalizeAppName(string? name)
    {
        var token = (name ?? string.Empty).Trim().ToLowerInvariant();
        if (token.EndsWith(".exe", StringComparison.Ordinal))
        {
            token = token[..^".exe".Length];
        }

        return token switch
        {
            "edge" or "microsoft edge" => "msedge",
            "explorador" or "archivos" or "files" or "file explorer" => "explorer",
            "calculadora" or "calculator" => "calc",
            "bloc" or "notepad" => "notepad",
            "terminal" or "consola" => "wt",
            "navegador" or "browser" => "msedge",
            _ => token,
        };
    }

    private static string LaunchToken(string token) => token switch
    {
        "wt" => "wt.exe",
        _ => token,
    };
}
