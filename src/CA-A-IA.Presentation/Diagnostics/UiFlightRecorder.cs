// TEMPORARY-DIAGNOSTIC: registrador de vuelo para el reporte de congelado no reproducible.
// Latido del hilo UI cada 2 s + marcadores de acciones en %TEMP%\caaia-flight-*.log.
// QUITAR cuando el cuelgue esté diagnosticado (no es telemetría: ficheros locales efímeros).

using Microsoft.UI.Dispatching;

namespace CaAIA.Presentation.Diagnostics;

/// <summary>Flight recorder mínimo: si el hilo UI se bloquea, el latido se detiene.</summary>
public sealed class UiFlightRecorder
{
    private readonly string _beatsPath;
    private readonly string _actionsPath;
    private DispatcherQueueTimer? _timer;

    public UiFlightRecorder()
    {
        var temp = Path.GetTempPath();
        _beatsPath = Path.Combine(temp, "caaia-flight-beats.log");
        _actionsPath = Path.Combine(temp, "caaia-flight-actions.log");
    }

    public void Start()
    {
        try
        {
            File.AppendAllText(_beatsPath,
                $"--- run {DateTimeOffset.Now:O} pid={Environment.ProcessId} ---\n");
            File.AppendAllText(_actionsPath,
                $"--- run {DateTimeOffset.Now:O} pid={Environment.ProcessId} ---\n");
            var queue = DispatcherQueue.GetForCurrentThread();
            _timer = queue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += (_, _) => Beat();
            _timer.Start();
            Action("recorder-started");
        }
        catch (Exception)
        {
        }
    }

    public void Action(string name)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} thread={Environment.CurrentManagedThreadId} {name}\n";
            File.AppendAllText(_actionsPath, line);
            Rotate(_actionsPath);
        }
        catch (Exception)
        {
        }
    }

    private void Beat()
    {
        try
        {
            File.AppendAllText(_beatsPath,
                $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} thread={Environment.CurrentManagedThreadId} BEAT\n");
            Rotate(_beatsPath);
        }
        catch (Exception)
        {
            try
            {
                _timer?.Stop();
            }
            catch (Exception)
            {
            }
        }
    }

    private static void Rotate(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 200_000)
            {
                File.WriteAllText(path, $"--- rotated {DateTimeOffset.Now:O} ---\n");
            }
        }
        catch (Exception)
        {
        }
    }
}
