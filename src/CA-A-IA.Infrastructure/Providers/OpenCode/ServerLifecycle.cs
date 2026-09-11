// CA-A-IA — Ciclo de vida del servidor OpenCode: localizar binario, puerto libre,
// `serve`, espera de health, parada. Testeable (lifecycle nulo + HttpClient simulado).

using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure.Providers.OpenCode;

/// <summary>Contrato mínimo del ciclo de vida (producción = proceso hijo; tests = nulo).</summary>
public interface IOpenCodeServerLifecycle : IAsyncDisposable
{
    /// <summary>Arranca (o localiza) el servidor y devuelve su base URL.</summary>
    Task<string> StartAsync(CancellationToken cancellationToken);
}

/// <summary>Lifecycle nulo para tests: el servidor ya existe en la URL dada.</summary>
public sealed class NullServerLifecycle : IOpenCodeServerLifecycle
{
    private readonly string _url;
    public NullServerLifecycle(string url) => _url = url;
    public Task<string> StartAsync(CancellationToken cancellationToken) => Task.FromResult(_url);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Gestiona `opencode serve --port P --hostname 127.0.0.1` como proceso hijo y espera
/// `/global/health`. Sin binario → <see cref="NotSupportedException"/> limpio (el proveedor
/// lo traduce a indisponibilidad, nunca a crash).
/// </summary>
public sealed class ProcessServerLifecycle : IOpenCodeServerLifecycle
{
    private readonly string _command;
    private readonly string _hostname;
    private readonly int _preferredPort;
    private readonly TimeSpan _startupTimeout;
    private readonly ILogger _log;
    private global::System.Diagnostics.Process? _process;
    private bool _disposed;

    public ProcessServerLifecycle(
        string command, string hostname, int preferredPort, TimeSpan startupTimeout, ILogger log)
    {
        _command = string.IsNullOrWhiteSpace(command) ? "opencode" : command;
        _hostname = hostname;
        _preferredPort = preferredPort;
        _startupTimeout = startupTimeout;
        _log = log;
    }

    /// <summary>
    /// Localiza el binario en PATH. En Windows solo devuelve ejecutables reales o scripts
    /// lanzables (.exe, .cmd, .bat, .ps1 — ver <see cref="Process.ScriptLaunch"/>); el nombre
    /// pelado (shim sh de npm) NO es ejecutable por CreateProcess y se omite.
    /// </summary>
    public static string? FindBinary(string command)
    {
        try
        {
            if (File.Exists(command) && IsRunnable(command))
            {
                return command;
            }

            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            var names = OperatingSystem.IsWindows()
                ? new[] { command + ".exe", command + ".cmd", command + ".bat", command + ".ps1" }
                : new[] { command };
            foreach (var dir in pathDirs)
            {
                foreach (var name in names)
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static bool IsRunnable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var ext = Path.GetExtension(path);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var binary = FindBinary(_command)
            ?? throw new NotSupportedException(
                $"OpenCode binary '{_command}' not found in PATH. Install OpenCode to enable this provider.");
        var port = _preferredPort > 0 ? _preferredPort : FreePort();
        var url = $"http://{_hostname}:{port}";

        // ¿Ya hay un servidor sano ahí? (p. ej. TUI en marcha): reutilizar, no duplicar.
        if (await ProbeAsync(url, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
        {
            _log.LogInformation("Reusing existing OpenCode server at {Url}", url);
            return url;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            WorkingDirectory = Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var (file, launchArgs) = Infrastructure.Process.ScriptLaunch.Wrap(binary,
            new[] { "serve", "--port", port.ToString(), "--hostname", _hostname });
        startInfo.FileName = file;
        foreach (var a in launchArgs)
        {
            startInfo.ArgumentList.Add(a);
        }

        _process = new global::System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start 'opencode serve'.");
        }

        _log.LogInformation("Started 'opencode serve' (pid {Pid}) at {Url}", _process.Id, url);
        var deadline = DateTimeOffset.UtcNow + _startupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                var err = await _process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"'opencode serve' exited during startup (code {_process.ExitCode}): {Truncate(err, 500)}");
            }

            if (await ProbeAsync(url, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
            {
                return url;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"OpenCode server at {url} did not become healthy in {_startupTimeout}.");
    }

    private static async Task<bool> ProbeAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = timeout };
            // Convención OpenCode: OPENCODE_SERVER_USERNAME (defecto "opencode") + OPENCODE_SERVER_PASSWORD.
            // Sin password en entorno, sonda anónima (servidores sin auth la aceptan).
            var username = Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME");
            var password = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/global/health");
            if (!string.IsNullOrEmpty(password))
            {
                var basic = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{(string.IsNullOrEmpty(username) ? "opencode" : username)}:{password}"));
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
            }

            using var response = await http.SendAsync(req, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_process is null || _process.HasExited)
        {
            _process?.Dispose();
            return;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
#if NET5_0_OR_GREATER
            await _process.WaitForExitAsync().ConfigureAwait(false);
#endif
        }
        catch (Exception)
        {
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] + "…" : text;
}
