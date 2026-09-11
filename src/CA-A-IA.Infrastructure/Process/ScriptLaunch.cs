// CA-A-IA — Lanzamiento de scripts (shims npm .cmd/.ps1) que CreateProcess no ejecuta directos.

using System.Collections.ObjectModel;

namespace CaAIA.Infrastructure.Process;

/// <summary>
/// Envuelve binarios scripteados para ejecutarlos sin shell interactivo:
/// `.cmd`/`.bat` vía `cmd.exe /d /c` y `.ps1` vía powershell. Los .exe pasan intactos.
/// </summary>
internal static class ScriptLaunch
{
    public static (string FileName, IReadOnlyList<string> Arguments) Wrap(
        string command, IReadOnlyList<string> arguments)
    {
        var ext = Path.GetExtension(command);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var args = new List<string> { "/d", "/c", command };
            args.AddRange(arguments);
            return ("cmd.exe", new ReadOnlyCollection<string>(args));
        }

        if (ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", command };
            args.AddRange(arguments);
            return ("powershell.exe", new ReadOnlyCollection<string>(args));
        }

        return (command, arguments);
    }
}
