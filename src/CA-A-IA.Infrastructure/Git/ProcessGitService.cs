// CA-A-IA — Git vía procesos (sin acoplar el núcleo a comandos git, §21).
// TODO(FUTURE_PHASE): revert, stash, remotos.

using CaAIA.Domain.Enums;
using CaAIA.Domain.Git;

namespace CaAIA.Infrastructure.Git;

/// <summary>
/// <see cref="IGitService"/> real basado en el CLI `git` con timeouts y truncado de salida.
/// Soporta status/diff/log/commit/branches/checkout (con guarda de working limpio).
/// </summary>
public sealed class ProcessGitService : IGitService
{
    private readonly Process.ProcessRunner _runner;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public ProcessGitService(Process.ProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task<bool> IsRepositoryAsync(string workspacePath, CancellationToken cancellationToken)
    {
        try
        {
            var r = await _runner.RunAsync("git", new[] { "rev-parse", "--is-inside-work-tree" },
                workspacePath, Timeout, cancellationToken).ConfigureAwait(false);
            return r.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<GitStatus> GetStatusAsync(string workspacePath, CancellationToken cancellationToken)
    {
        try
        {
            var branchTask = _runner.RunAsync("git", new[] { "branch", "--show-current" },
                workspacePath, Timeout, cancellationToken);
            var porcelainTask = _runner.RunAsync("git", new[] { "status", "--porcelain=v1" },
                workspacePath, Timeout, cancellationToken);
            await Task.WhenAll(branchTask, porcelainTask).ConfigureAwait(false);

            var branch = branchTask.Result.Success ? branchTask.Result.StandardOutput.Trim() : null;
            var changes = new List<GitChange>();
            if (porcelainTask.Result.Success)
            {
                foreach (var line in porcelainTask.Result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Length > 3)
                    {
                        changes.Add(new GitChange(line[3..].Trim(), line[..2].Trim()));
                    }
                }
            }

            return new GitStatus(workspacePath, branch, changes.Count > 0, changes);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"git status failed: {ex.Message}", ex);
        }
    }

    public async Task<string> GetDiffAsync(string workspacePath, int maxChars, CancellationToken cancellationToken)
    {
        var r = await _runner.RunAsync("git", new[] { "diff", "--no-color" },
            workspacePath, Timeout, cancellationToken).ConfigureAwait(false);
        if (!r.Success)
        {
            throw new InvalidOperationException($"git diff failed: {r.StandardError}");
        }

        var diff = r.StandardOutput;
        return diff.Length > maxChars ? diff[..maxChars] + "\n…[truncated]" : diff;
    }

    public async Task<IReadOnlyList<GitCommit>> GetLogAsync(
        string workspacePath, int maxEntries, CancellationToken cancellationToken)
    {
        // Formato estable con separador ASCII RS para parseo robusto.
        var r = await _runner.RunAsync("git",
            new[] { "log", $"-n{Math.Clamp(maxEntries, 1, 200)}", "--format=%H%x1f%h%x1f%an%x1f%cI%x1f%s" },
            workspacePath, Timeout, cancellationToken).ConfigureAwait(false);
        if (!r.Success)
        {
            throw new InvalidOperationException($"git log failed: {r.StandardError}");
        }

        var commits = new List<GitCommit>();
        foreach (var line in r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\x1f');
            if (parts.Length != 5)
            {
                continue;
            }

            if (!DateTimeOffset.TryParse(parts[3], out var date))
            {
                date = DateTimeOffset.MinValue;
            }

            commits.Add(new GitCommit(parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), date, parts[4].Trim()));
        }

        return commits;
    }

    public async Task<string> CommitAsync(string workspacePath, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Commit message is required.", nameof(message));
        }

        // Solo confirma lo ya preparado en el índice; jamás añade ficheros por su cuenta.
        var r = await _runner.RunAsync("git", new[] { "commit", "-m", message },
            workspacePath, Timeout, cancellationToken).ConfigureAwait(false);
        if (!r.Success)
        {
            throw new InvalidOperationException($"git commit failed: {r.StandardError}");
        }

        return r.StandardOutput.Trim();
    }

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
        string workspacePath, CancellationToken cancellationToken)
    {
        var r = await _runner.RunAsync("git", new[] { "branch", "--format=%(refname:short)%09%(HEAD)" },
            workspacePath, Timeout, cancellationToken).ConfigureAwait(false);
        if (!r.Success)
        {
            throw new InvalidOperationException($"git branch failed: {r.StandardError}");
        }

        var branches = new List<GitBranch>();
        foreach (var line in r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length != 2)
            {
                continue;
            }

            branches.Add(new GitBranch(parts[0].Trim(), parts[1].Trim() == "*"));
        }

        return branches;
    }

    public async Task CreateBranchAsync(string workspacePath, string name, CancellationToken cancellationToken)
    {
        ValidateRefName(name);

        // Sin shell (ArgumentList) y sin prefijo `-` validado arriba: no hay inyección
        // de opciones. Sin `--`: en `checkout`, `--` cambiaría la semántica a
        // restauración de pathspec en vez de cambio de rama.
        var r = await _runner.RunAsync("git", new[] { "branch", name },
            workspacePath, Timeout, cancellationToken).ConfigureAwait(false);
        if (!r.Success)
        {
            throw new InvalidOperationException($"git branch failed: {r.StandardError}");
        }
    }

    public async Task CheckoutAsync(string workspacePath, string name, CancellationToken cancellationToken)
    {
        ValidateRefName(name);

        var status = await GetStatusAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        if (status.HasChanges)
        {
            throw new InvalidOperationException(
                $"Refusing checkout to '{name}' with a dirty working tree ({status.Changes.Count} changes).");
        }

        var r = await _runner.RunAsync("git", new[] { "checkout", name },
            workspacePath, Timeout, cancellationToken).ConfigureAwait(false);
        if (!r.Success)
        {
            throw new InvalidOperationException($"git checkout failed: {r.StandardError}");
        }
    }

    /// <summary>
    /// Nombres de rama/checkout: sin prefijo `-` (evita inyección de opciones aunque
    /// <c>ArgumentList</c> ya evite el shell) y charset de ref seguro.
    /// </summary>
    internal static void ValidateRefName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Branch name is required.", nameof(name));
        }

        if (name.StartsWith('-') || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsafe git ref name: '{name}'.", nameof(name));
        }

        foreach (var c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c is '.' or '/' or '_' or '-' or '+'))
            {
                throw new ArgumentException($"Unsafe character '{c}' in git ref name.", nameof(name));
            }
        }
    }
}
