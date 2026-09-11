// CA-A-IA · Fase 0 — Tests del dominio de planificación: invariantes de Plan y AgentTask.

using CaAIA.Domain.Enums;
using CaAIA.Domain.Planning;

namespace CaAIA.Tests.Unit;

public sealed class PlanDomainTests
{
    private static Plan DraftPlan() => new()
    {
        SessionId = Guid.NewGuid(),
        Goal = "Build feature X",
        Requirements = new[] { "Implement feature X with tests" },
        FinalAcceptanceCriteria = new[] { "All tests pass" },
        Tasks = new List<AgentTask>
        {
            new() { Title = "Implement X", AcceptanceCriteria = new[] { "Build succeeds" } },
        },
    };

    [Fact]
    public void Approve_RequiresReviewState()
    {
        var plan = DraftPlan();
        Assert.Throws<InvalidOperationException>(() => plan.MarkApproved());
        plan.MarkInReview();
        Assert.Equal(PlanStatus.InReview, plan.Status);
    }

    [Fact]
    public void Approve_BlockedByOpenQuestions()
    {
        var plan = DraftPlan();
        plan.SetQuestions(new[] { new Clarification { Question = "Which framework?" } });
        plan.MarkInReview();
        Assert.True(plan.HasOpenQuestions);
        Assert.Throws<InvalidOperationException>(() => plan.MarkApproved());
    }

    [Fact]
    public void Approve_Succeeds_WhenQuestionsAnsweredAndTasksExist()
    {
        var plan = DraftPlan();
        var q = new Clarification { Question = "Which framework?" };
        plan.SetQuestions(new[] { q });
        plan.MarkInReview();
        q.AnswerQuestion("Latest LTS");
        plan.MarkApproved();
        plan.MarkExecuting();
        Assert.Equal(PlanStatus.Executing, plan.Status);
    }

    [Fact]
    public void Approve_EmptyPlan_Throws()
    {
        var plan = new Plan { SessionId = Guid.NewGuid(), Goal = "Empty" };
        plan.MarkInReview();
        Assert.Throws<InvalidOperationException>(() => plan.MarkApproved());
    }

    [Fact]
    public void FinalAudit_CanExtendPlan_OnlyWhileAuditing()
    {
        var plan = DraftPlan();
        Assert.Throws<InvalidOperationException>(() =>
            plan.ExtendAfterAudit(new[] { new AgentTask { Title = "Extra" } }));

        plan.MarkInReview();
        plan.MarkApproved();
        plan.MarkExecuting();
        foreach (var t in plan.Tasks)
        {
            t.MarkStarted();
            t.MarkCompleted();
        }

        Assert.True(plan.AllTasksClosed());
        plan.MarkAuditing();
        plan.ExtendAfterAudit(new[] { new AgentTask { Title = "Audit gap: docs" } });
        Assert.Equal(2, plan.Revision);
        Assert.Equal(PlanStatus.Executing, plan.Status);
        Assert.Equal(2, plan.Tasks.Count);
    }

    [Fact]
    public void Task_IsReady_RespectsDependencies()
    {
        var a = new AgentTask { Title = "A" };
        var b = new AgentTask { Title = "B", DependsOn = new[] { a.Id } };
        var statuses = new Dictionary<Guid, AgentTaskStatus>
        {
            [a.Id] = AgentTaskStatus.Pending,
            [b.Id] = AgentTaskStatus.Pending,
        };
        Assert.True(a.IsReady(statuses));
        Assert.False(b.IsReady(statuses));
        statuses[a.Id] = AgentTaskStatus.Completed;
        Assert.True(b.IsReady(statuses));
    }

    [Fact]
    public void Task_CannotComplete_WithoutStart()
    {
        var task = new AgentTask { Title = "T" };
        Assert.Throws<InvalidOperationException>(() => task.MarkCompleted());
        task.MarkStarted();
        Assert.Equal(1, task.Attempts);
        task.MarkCompleted();
        Assert.Equal(AgentTaskStatus.Completed, task.Status);
    }

    [Fact]
    public void Task_CanRetry_UntilMaxAttempts()
    {
        var task = new AgentTask { Title = "T", MaxAttempts = 2 };
        task.MarkStarted();
        task.MarkFailed("boom", FailureCategory.CodeError);
        Assert.True(task.CanRetry);
        task.MarkStarted();
        task.MarkFailed("boom", FailureCategory.CodeError);
        Assert.False(task.CanRetry);
    }
}
