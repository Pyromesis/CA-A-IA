// CA-A-IA — Ratón y teclado reales vía SendInput, con movimiento HUMANIZADO:
// curva ease-in-out (acelera/frena como una mano), jitter, velocidad variable y
// pausas: nada de saltos instantáneos de robot.

using System.Runtime.InteropServices;
using CaAIA.Domain.Interaction;

namespace CaAIA.Infrastructure.Process;

/// <summary>
/// <see cref="IUiAutomation"/> con Win32 SendInput. Monitor principal; toda
/// coordenada se recorta a la pantalla. Los delays observan el token.
/// </summary>
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

        var steps = (int)Math.Clamp(distance / 8, 12, 120);
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
            await Task.Delay(Random.Shared.Next(8, 17), ct).ConfigureAwait(false);
        }

        // Micro-pausa de "dwell" antes de actuar, como un humano que apunta.
        await Task.Delay(Random.Shared.Next(60, 180), ct).ConfigureAwait(false);
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
            await Task.Delay(Random.Shared.Next(40, 110), ct).ConfigureAwait(false);
            SendMouse(up, 0);
            if (i + 1 < clicks)
            {
                await Task.Delay(Random.Shared.Next(60, 130), ct).ConfigureAwait(false);
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
        // Ritmo humano: 25–95 ms por tecla + pausas de "pensar" ocasionales.
        var count = 0;
        foreach (var ch in text ?? string.Empty)
        {
            ct.ThrowIfCancellationRequested();
            if (count >= 2000)
            {
                break;
            }

            SendUnicode(ch, keyUp: false);
            await Task.Delay(Random.Shared.Next(15, 45), ct).ConfigureAwait(false);
            SendUnicode(ch, keyUp: true);
            count++;
            await Task.Delay(Random.Shared.Next(25, 95)
                + (Random.Shared.Next(100) < 5 ? Random.Shared.Next(250, 600) : 0), ct)
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
}
