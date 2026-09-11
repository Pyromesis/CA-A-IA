// CA-A-IA — Adaptador ICommandRunner sobre ProcessRunner (el Agent no conoce procesos).

using CaAIA.Domain.Process;

namespace CaAIA.Infrastructure.Process;

/// <summary>Puente Domain → ProcessRunner (timeout + cancelación + árbol).</summary>
public sealed class CommandRunnerAdapter : ICommandRunner
{
    private readonly ProcessRunner _runner;

    public CommandRunnerAdapter(ProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(fileName, arguments, workingDirectory, timeout, cancellationToken)
            .ConfigureAwait(false);
        return new CommandResult(result.ExitCode, result.StandardOutput, result.StandardError, result.Duration);
    }
}
