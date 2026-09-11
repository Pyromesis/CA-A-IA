// CA-A-IA — Tests de Git (log + commit) sobre un repo temporal real.

using CaAIA.Infrastructure.Git;
using CaAIA.Infrastructure.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaAIA.Tests.Integration;

public sealed class GitServiceTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"ca-a-ia-git-{Guid.NewGuid():N}");
    private readonly ProcessGitService _git = new(new ProcessRunner());

    public GitServiceTests()
    {
        Directory.CreateDirectory(_repo);
        Run("init", "-b", "main").GetAwaiter().GetResult();
        Run("config", "user.email", "test@ca-a-ia.local").GetAwaiter().GetResult();
        Run("config", "user.name", "CA-A-IA Test").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (Exception) { }
    }

    private async Task Run(params string[] args)
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("git", args, _repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(result.Success, result.StandardError);
    }

    [Fact]
    public async Task StatusDiffLogCommit_WorkOnTempRepo()
    {
        Assert.True(await _git.IsRepositoryAsync(_repo, CancellationToken.None));
        Assert.False(await _git.IsRepositoryAsync(Path.GetTempPath(), CancellationToken.None));

        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "v1");
        await Run("add", "a.txt");
        var output = await _git.CommitAsync(_repo, "first commit", CancellationToken.None);
        Assert.Contains("first commit", output);

        var status = await _git.GetStatusAsync(_repo, CancellationToken.None);
        Assert.False(status.HasChanges);
        Assert.Equal("main", status.Branch);

        var log = await _git.GetLogAsync(_repo, 10, CancellationToken.None);
        var commit = Assert.Single(log);
        Assert.Equal("first commit", commit.Subject);
        Assert.Equal("CA-A-IA Test", commit.Author);
        Assert.Equal(40, commit.Hash.Length);

        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "v2");
        var status2 = await _git.GetStatusAsync(_repo, CancellationToken.None);
        Assert.True(status2.HasChanges);
        var diff = await _git.GetDiffAsync(_repo, 10_000, CancellationToken.None);
        Assert.Contains("v2", diff);
    }

    [Fact]
    public async Task Commit_EmptyMessage_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _git.CommitAsync(_repo, "  ", CancellationToken.None));
    }

    [Fact]
    public async Task Branches_CreateListCheckout_WithDirtyGuard()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "x");
        await Run("add", "b.txt");
        await _git.CommitAsync(_repo, "base", CancellationToken.None);

        await _git.CreateBranchAsync(_repo, "feature", CancellationToken.None);
        var branches = await _git.GetBranchesAsync(_repo, CancellationToken.None);
        Assert.Contains(branches, b => b.Name == "feature" && !b.IsCurrent);
        Assert.Contains(branches, b => b.IsCurrent);

        await _git.CheckoutAsync(_repo, "feature", CancellationToken.None);
        branches = await _git.GetBranchesAsync(_repo, CancellationToken.None);
        Assert.Contains(branches, b => b.Name == "feature" && b.IsCurrent);

        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "dirty");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _git.CheckoutAsync(_repo, "main", CancellationToken.None));
    }
}
