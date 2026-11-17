using AgenticForge.Domain;
using Xunit;

namespace AgenticForge.Contracts.Tests;

public class LifecycleTests
{
    private static WorkflowRun NewRun() => WorkflowRun.Create(new WorkflowRunId("r1"), Samples.Definition(), "corr", Samples.T0);

    [Fact]
    public void WorkflowRun_HappyPath_WithPause()
    {
        var run = NewRun();
        run.Start(Samples.T0, new StepId("a"));
        run.Pause();
        Assert.Equal(WorkflowRunStatus.Paused, run.Status);
        run.Resume();
        run.Succeed(Samples.T0.AddMinutes(1));
        Assert.True(run.IsTerminal);
        Assert.Equal(Samples.T0.AddMinutes(1), run.State.EndedAt);
    }

    [Fact]
    public void WorkflowRun_TerminalStatesAreFinal()
    {
        var run = NewRun();
        run.Start(Samples.T0, new StepId("a"));
        run.Fail(Samples.T0, new Failure(FailureKind.Provider, "x", "boom"));
        Assert.Throws<InvalidLifecycleTransitionException>(() => run.Succeed(Samples.T0));
        Assert.Throws<InvalidLifecycleTransitionException>(() => run.Cancel(Samples.T0));
    }

    [Fact]
    public void Cancellation_IsNotFailure()
    {
        var run = NewRun();
        run.Cancel(Samples.T0, "user request"); // legal while still Pending
        Assert.Equal(WorkflowRunStatus.Cancelled, run.Status);
        Assert.Null(run.State.Failure);
        Assert.Equal("user request", run.State.CancellationReason);
    }

    [Fact]
    public void WorkflowRun_RejectsIllegalOrder()
    {
        var run = NewRun();
        Assert.Throws<InvalidLifecycleTransitionException>(() => run.Pause());
        Assert.Throws<InvalidLifecycleTransitionException>(() => run.Succeed(Samples.T0));
    }

    [Fact]
    public void StepRun_IsIndependentOfWorkflowRun_AndRetriesAreNewAttempts()
    {
        var first = StepRun.Create(new StepRunId("s1"), new WorkflowRunId("r1"), new StepId("a"));
        first.Start(Samples.T0);
        first.Fail(Samples.T0, new Failure(FailureKind.Provider, "rate", "limited", Retryable: true));
        Assert.Throws<InvalidLifecycleTransitionException>(() => first.Start(Samples.T0));

        var second = StepRun.Create(new StepRunId("s2"), new WorkflowRunId("r1"), new StepId("a"), attempt: 2);
        Assert.Equal(StepRunStatus.Pending, second.Status);
        Assert.Equal(2, second.State.Attempt);
    }

    [Fact]
    public void StepRun_PauseForInputThenSucceedWithOutcome()
    {
        var s = StepRun.Create(new StepRunId("s1"), new WorkflowRunId("r1"), new StepId("a"));
        s.Start(Samples.T0);
        s.AwaitInput();
        Assert.Throws<InvalidLifecycleTransitionException>(() => s.Succeed(Samples.T0, "ready"));
        s.Resume();
        s.Succeed(Samples.T0, "ready");
        Assert.Equal("ready", s.State.Outcome);
    }

    [Fact]
    public void StepRun_ResumeFromApproval_RecordsDecision()
    {
        var s = StepRun.Create(new StepRunId("s1"), new WorkflowRunId("r1"), new StepId("b"));
        s.Start(Samples.T0);
        s.AwaitApproval();
        Assert.Throws<ArgumentException>(() => s.Resume());
        var decision = new ApprovalDecision("alice", ApprovalDecisionKind.Approved, Samples.T0, "looks good");
        s.Resume(decision);
        Assert.Equal(decision, s.State.Approval);
    }

    [Fact]
    public void StepRun_CancelFromAwaitingApproval()
    {
        var s = StepRun.Create(new StepRunId("s1"), new WorkflowRunId("r1"), new StepId("b"));
        s.Start(Samples.T0);
        s.AwaitApproval();
        s.Cancel(Samples.T0);
        Assert.Equal(StepRunStatus.Cancelled, s.Status);
    }

    [Fact]
    public void Rehydrate_RestoresState()
    {
        var run = NewRun();
        run.Start(Samples.T0, new StepId("a"));
        var copy = WorkflowRun.Rehydrate(run.State);
        copy.Pause();
        Assert.Equal(WorkflowRunStatus.Running, run.Status); // independent instances over immutable snapshots
    }
}
