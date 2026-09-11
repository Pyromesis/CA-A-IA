// CA-A-IA — Atajos GLOBALES de sistema (funcionan en cualquier app):
// Shift izq.+A = pausar al agente, Shift izq.+S = reanudar. Vía hook de teclado
// de bajo nivel (WH_KEYBOARD_LL): RegisterHotKey no distingue shift izq./der.
// No se traga NADA: las teclas siguen llegando a la app enfocada (escribir
// mayúsculas funciona igual); solo se dispara en el flanco de pulsación.

using System.Runtime.InteropServices;

namespace CaAIA.Infrastructure.Process;

/// <summary>
/// Hook LL en hilo propio con bucle de mensajes. Los callbacks llegan en ese
/// hilo: quien los recibe debe apoyarse en comandos del VM (ya marshalan a UI)
/// o en servicios thread-safe.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_QUIT = 0x0012;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_A = 0x41;
    private const int VK_S = 0x53;

    // KBDLLHOOKSTRUCT.flags: bit 7 = tecla soltada (transición).
    private const uint LLKHF_UP = 0x80;

    private const long DebounceTicks = TimeSpan.TicksPerMillisecond * 500;

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

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, nint hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly HookProc _hook;
    private Thread? _thread;
    private nint _hookHandle;
    private uint _hookThreadId;
    private volatile bool _running;
    private volatile bool _lShiftDown;
    private volatile bool _aDown;
    private volatile bool _sDown;
    private long _lastPauseTicks;
    private long _lastResumeTicks;
    private Func<Task>? _onPause;
    private Func<Task>? _onResume;
    private readonly ManualResetEventSlim _ready = new(false);
    private bool _disposed;

    public GlobalHotkeys()
    {
        // Fijar el delegado: si el GC lo mueve/libera, el hook muere.
        _hook = HookCallback;
    }

    /// <summary>Instala el hook. Devuelve false si el sistema lo deniega.</summary>
    public bool Start(Func<Task> onPause, Func<Task> onResume)
    {
        _onPause = onPause ?? throw new ArgumentNullException(nameof(onPause));
        _onResume = onResume ?? throw new ArgumentNullException(nameof(onResume));
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "CA-A-IA-hotkeys" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)) || _hookHandle == nint.Zero)
        {
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
            if (_hookHandle != nint.Zero)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = nint.Zero;
            }

            if (_hookThreadId != 0)
            {
                PostThreadMessage(_hookThreadId, WM_QUIT, nuint.Zero, nint.Zero);
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
            _hookThreadId = GetCurrentThreadId();
            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hook, nint.Zero, 0);
        }
        catch (Exception)
        {
            _hookHandle = nint.Zero;
        }
        finally
        {
            _ready.Set();
        }

        if (_hookHandle == nint.Zero)
        {
            return;
        }

        try
        {
            while (_running && GetMessage(out var msg, nint.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            try
            {
                if (_hookHandle != nint.Zero)
                {
                    UnhookWindowsHookEx(_hookHandle);
                    _hookHandle = nint.Zero;
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
            {
                var vk = Marshal.ReadInt32(lParam);
                var flags = (uint)Marshal.ReadInt32(lParam, 8);
                var up = (flags & LLKHF_UP) != 0;
                if (vk == VK_LSHIFT)
                {
                    _lShiftDown = !up;
                }
                else if (vk == VK_A || vk == VK_S)
                {
                    if (up)
                    {
                        if (vk == VK_A)
                        {
                            _aDown = false;
                        }
                        else
                        {
                            _sDown = false;
                        }
                    }
                    else if (IsLeftShiftHeld() && Edge(vk))
                    {
                        if (vk == VK_A)
                        {
                            Fire(_onPause, ref _lastPauseTicks);
                        }
                        else
                        {
                            Fire(_onResume, ref _lastResumeTicks);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        // Jamás tragar: todo sigue a la app enfocada.
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static bool IsLeftShiftHeld()
    {
        try
        {
            return (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Flanco de pulsación (una vez por bajada): filtra auto-repetición.</summary>
    private bool Edge(int vk)
    {
        if (vk == VK_A)
        {
            if (_aDown)
            {
                return false;
            }

            _aDown = true;
            return true;
        }

        if (_sDown)
        {
            return false;
        }

        _sDown = true;
        return true;
    }

    private static void Fire(Func<Task>? action, ref long lastTicks)
    {
        if (action is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.Ticks;
        if (now - Volatile.Read(ref lastTicks) < DebounceTicks)
        {
            return;
        }

        Volatile.Write(ref lastTicks, now);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _ready.Dispose();
        GC.SuppressFinalize(this);
    }
}
