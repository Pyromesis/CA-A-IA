// CA-A-IA — Atajos GLOBALES de sistema (funcionan en cualquier app):
// Ctrl+J = pausar al agente, Ctrl+K = reanudar. Vía RegisterHotKey + ventana
// solo-mensajes en hilo propio. Si el combo está ocupado, se sigue sin atajos.

using System.Runtime.InteropServices;

namespace CaAIA.Infrastructure.Process;

/// <summary>
/// Hotkeys de sistema con ventana <c>HWND_MESSAGE</c> en hilo dedicado.
/// Los callbacks llegan en ese hilo: quien los recibe debe apoyarse en comandos
/// del VM (ya marshalan a UI) o en servicios thread-safe.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_QUIT = 0x0012;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_J = 0x4A;
    private const uint VK_K = 0x4B;

    private static readonly nint HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, nint hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    private Thread? _thread;
    private nint _hwnd;
    private volatile bool _running;
    private Func<Task>? _onPause;
    private Func<Task>? _onResume;
    private readonly ManualResetEventSlim _ready = new(false);
    private bool _disposed;

    /// <summary>Registra Ctrl+J / Ctrl+K. Devuelve false si el sistema los deniega.</summary>
    public bool Start(Func<Task> onPause, Func<Task> onResume)
    {
        _onPause = onPause ?? throw new ArgumentNullException(nameof(onPause));
        _onResume = onResume ?? throw new ArgumentNullException(nameof(onResume));
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "CA-A-IA-hotkeys" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)) || _hwnd == nint.Zero)
        {
            Stop();
            return false;
        }

        if (!RegisterHotKey(_hwnd, 1, MOD_CONTROL | MOD_NOREPEAT, VK_J)
            || !RegisterHotKey(_hwnd, 2, MOD_CONTROL | MOD_NOREPEAT, VK_K))
        {
            // Ocupados por otra app: sin atajos, sin romper nada.
            Stop();
            return false;
        }

        return true;
    }

    public void Stop()
    {
        _running = false;
        try
        {
            if (_hwnd != nint.Zero)
            {
                UnregisterHotKey(_hwnd, 1);
                UnregisterHotKey(_hwnd, 2);
                PostMessage(_hwnd, WM_QUIT, nuint.Zero, nint.Zero);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }

        _thread = null;
    }

    private void Loop()
    {
        try
        {
            _hwnd = CreateWindowEx(0, "STATIC", "CA-A-IA-hotkeys", 0,
                0, 0, 0, 0, HWND_MESSAGE, nint.Zero, GetModuleHandle(null), nint.Zero);
        }
        catch (Exception)
        {
            _hwnd = nint.Zero;
        }
        finally
        {
            _ready.Set();
        }

        if (_hwnd == nint.Zero)
        {
            return;
        }

        try
        {
            while (_running && GetMessage(out var msg, nint.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY)
                {
                    var id = (int)msg.wParam;
                    var action = id == 1 ? _onPause : id == 2 ? _onResume : null;
                    if (action is not null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await action().ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                            }
                        });
                    }
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            if (_hwnd != nint.Zero)
            {
                try
                {
                    DestroyWindow(_hwnd);
                }
                catch (Exception)
                {
                }

                _hwnd = nint.Zero;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _ready.Dispose();
    }
}
