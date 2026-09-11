// CA-A-IA — Abstracción de ejecución de comandos (el Agent la consume sin conocer ProcessRunner).

namespace CaAIA.Domain.Process;

/// <summary>Resultado de un comando ejecutado.</summary>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Ejecuta procesos con timeout + cancelación. Implementación en Infrastructure (ProcessRunner).
/// </summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
