// CA-A-IA · Fase 0 — Tests de herramientas, auditor final y memoria.

using CaAIA.Agent.Verification;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Memory;
using CaAIA.Domain.Planning;
using CaAIA.Infrastructure.Memory;
using CaAIA.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaAIA.Tests.Unit;

public sealed class ToolsAndAuditTests
{
    [Fact]
    public async Task ReadFileTool_ReadsWithinLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ca-a-ia-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "hello world");
        try
        {
            var tool = new ReadFileTool();
            var result = await tool.ExecuteAsync(
                new Domain.Tools.ToolInvocation(Guid.NewGuid(), ReadFileTool.ToolId,
                    $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(path)}}}""",
                    new Domain.Correlation.CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())),
                CancellationToken.None);
            Assert.True(result.Success, result.Error);
            Assert.Equal("hello world", result.Output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadFileTool_MissingPathArg_FailsCleanly()
    {
        var tool = new ReadFileTool();
        var result = await tool.ExecuteAsync(
            new Domain.Tools.ToolInvocation(Guid.NewGuid(), ReadFileTool.ToolId, "{}",
                new Domain.Correlation.CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())),
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(FailureCategory.ToolFailure, result.FailureCategory);
    }

    [Fact]
    public void ToolRegistry_FiltersByGrant()
    {
        var registry = new ToolRegistry();
        registry.Register(new ReadFileTool());
        registry.Register(new ListDirectoryTool());
        Assert.Equal(2, registry.ListDefinitions().Count);
        Assert.Equal(2, registry.ListDefinitionsFor(ToolPermission.Read).Count);
        Assert.Empty(registry.ListDefinitionsFor(ToolPermission.None));
        Assert.Throws<KeyNotFoundException>(() => registry.Get("Nope"));
    }

    [Fact]
    public async Task FinalAudit_DetectsUncoveredRequirement()
    {
        var auditor = new FinalPlanAuditor(NullLogger<FinalPlanAuditor>.Instance);
        var plan = new Plan
        {
            SessionId = Guid.NewGuid(),
            Goal = "Add login with tests",
            Requirements = new[] { "Implement user login flow", "Add quantum teleportation module" },
            FinalAcceptanceCriteria = new[] { "All tests pass" },
            Tasks = new List<AgentTask>
            {
                new() { Title = "Implement user login flow", Description = "login flow with tests" },
            },
        };
        var report = await auditor.AuditAsync(plan.Goal, plan, new[] { "All tests pass: 12/12" }, CancellationToken.None);
        Assert.False(report.IsSatisfied);
        Assert.Contains(report.MissingRequirements, m => m.Contains("teleportation"));
    }

    [Fact]
    public async Task FinalAudit_Satisfied_WhenCovered()
    {
        var auditor = new FinalPlanAuditor(NullLogger<FinalPlanAuditor>.Instance);
        var plan = new Plan
        {
            SessionId = Guid.NewGuid(),
            Goal = "Add login",
            Requirements = new[] { "Implement user login flow" },
            FinalAcceptanceCriteria = Array.Empty<string>(),
            Tasks = new List<AgentTask>
            {
                new() { Title = "Implement user login flow", Description = "login flow" },
            },
        };
        var report = await auditor.AuditAsync(plan.Goal, plan, Array.Empty<string>(), CancellationToken.None);
        Assert.True(report.IsSatisfied);
        Assert.Empty(report.MissingRequirements);
    }

    [Fact]
    public async Task MemoryStore_PutRecallExpire_Works()
    {
        var store = new InMemoryMemoryStore();
        var session = Guid.NewGuid();
        await store.PutAsync(new MemoryEntry(Guid.NewGuid(), MemoryScope.Error, session, null,
            "build", "missing SDK", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);
        await store.PutAsync(new MemoryEntry(Guid.NewGuid(), MemoryScope.Error, session, null,
            "old", "stale", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1)), CancellationToken.None);

        var recalled = await store.RecallAsync(session, MemoryScope.Error, null, 10, CancellationToken.None);
        Assert.Single(recalled);
        Assert.Equal("build", recalled[0].Key);

        var pruned = await store.PruneExpiredAsync(session, CancellationToken.None);
        Assert.Equal(1, pruned);
    }
}
