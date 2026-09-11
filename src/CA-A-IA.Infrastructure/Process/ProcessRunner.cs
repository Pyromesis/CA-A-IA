// CA-A-IA · Fase 0 — Ejecución de procesos con timeout + cancelación (base de git/build/tests).

using System.Diagnostics;
using System.Text;

namespace CaAIA.Infrastructure.Process;

/// <summary>Resultado de un proceso ejecutado.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration)
{
    public bool Success => ExitCode == 0;
}

    /// <summary>
    /// Ejecuta procesos sin bloquear hilos (salida async), con timeout y <see cref="CancellationToken"/>.
    /// El árbol de procesos se corta al cancelar/timeout (<c>Kill(entireProcessTree: true)</c>).
    /// La salida se acota en streaming (<see cref="MaxOutputChars"/>) para que un proceso
    /// verborreico no agote la memoria: lo que sobra se descarta con marca de truncado.
    /// </summary>
    public sealed class ProcessRunner
{
    private const int MaxOutputChars = 2_000_000;
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments)
        {
            startInfo.ArgumentList.Add(a);
        }

        if (environment is not null)
        {
            foreach (var (k, v) in environment)
            {
                startInfo.Environment[k] = v;
            }
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var startedAt = DateTimeOffset.UtcNow;

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }

        var stdoutTask = ReadCappedAsync(process.StandardOutput, stdout, cancellationToken);
        var stderrTask = ReadCappedAsync(process.StandardError, stderr, cancellationToken);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout: matar árbol y reportar como fallo clasificado por el llamador.
            TryKill(process);
            throw new TimeoutException($"Process '{fileName}' exceeded timeout of {timeout}.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var truncated = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        var (outText, outTruncated) = Flush(stdout, truncated[0]);
        var (errText, errTruncated) = Flush(stderr, truncated[1]);
        if (outTruncated || errTruncated)
        {
            const string mark = "\n…[truncated:process-output-capped]";
            return new ProcessResult(process.ExitCode, outText, errText + mark,
                DateTimeOffset.UtcNow - startedAt);
        }

        return new ProcessResult(process.ExitCode, outText, errText,
            DateTimeOffset.UtcNow - startedAt);
    }

    private static (string Text, bool Truncated) Flush(StringBuilder sink, bool truncated)
    {
        if (!truncated)
        {
            return (sink.ToString(), false);
        }

        // Deja sitio para la marca de truncado.
        const string mark = "\n…[truncated:process-output-capped]";
        var text = sink.ToString();
        return (text.Length > mark.Length ? text[..^mark.Length] + mark : mark, true);
    }

    /// <summary>Lee hasta EOF acotando el acumulado; devuelve true si hubo truncado.</summary>
    private static async Task<bool> ReadCappedAsync(StreamReader reader, StringBuilder sink, CancellationToken ct)
    {
        char[] buffer = new char[4096];
        int read;
        var truncated = false;
        while ((read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            var room = MaxOutputChars - sink.Length;
            if (room <= 0)
            {
                truncated = true;
                continue; // seguir drenando para no bloquear al hijo, sin acumular
            }

            sink.Append(buffer, 0, Math.Min(read, room));
            if (read > room)
            {
                truncated = true;
            }
        }

        return truncated;
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best-effort: el proceso ya pudo terminar.
        }
    }
}
