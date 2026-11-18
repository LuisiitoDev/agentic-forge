using AgenticForge.Abstractions;
using AgenticForge.Application;
using AgenticForge.Domain;
using AgenticForge.Runtime;
using static AgenticForge.Runtime.Tests.W;

namespace AgenticForge.Runtime.Tests;

public class OrchestratorTests
{
    private static AgentInvocationResult Ok(string outcome, string? output = null)
        => AgentInvocationResult.Success(outcome, output is null ? null : Json(output));

    private static FakeAgents TwoAgents(string idA = "agent-a", string idB = "agent-b")
        => new FakeAgents().Add("agent-a", FakeAgent.Returning(idA, Ok("ok", "{\"n\":1}")))
                           .Add("agent-b", FakeAgent.Returning(idB, Ok("done", "{\"n\":2}")));

    // ---- happy path & determinism

    [Fact]
    public async Task Linear_workflow_runs_in_transition_order_to_success()
    {
        var h = Build(TwoStep(), TwoAgents());
        var run = await h.Start();

        Assert.Equal(WorkflowRunStatus.Succeeded, run.Status);
        Assert.Equal(["run wf@1.0.0",
                      "  step a attempt=1 Succeeded outcome=ok",
                      "  step b attempt=1 Succeeded outcome=done",
                      "  => Succeeded"], await h.Trace(run));
        Assert.Equal(2, run.Output!.Value.GetProperty("n").GetInt32()); // output of the last step
        Assert.Equal(run, await h.Store.GetRunAsync(run.Id, default));
    }

    [Fact]
    public async Task Same_definition_gives_identical_trace_regardless_of_agent_implementation()
    {
        var h1 = Build(TwoStep(), TwoAgents("vendor-one", "vendor-one"));
        var h2 = Build(TwoStep(), TwoAgents("vendor-two", "vendor-two"));
        Assert.Equal(await h1.Trace(await h1.Start()), await h2.Trace(await h2.Start()));
    }

    [Fact]
    public async Task Step_input_carries_run_input_and_prior_outputs()
    {
        var b = FakeAgent.Returning("agent-b", Ok("done"));
        var agents = new FakeAgents().Add("agent-a", FakeAgent.Returning("agent-a", Ok("ok", "{\"x\":7}"))).Add("agent-b", b);
        var h = Build(TwoStep(), agents);
        await h.Start(input: Json("{\"issue\":42}"));

        var input = b.Calls.Single().Input;
        Assert.Equal(42, input.GetProperty("input").GetProperty("issue").GetInt32());
        Assert.Equal(7, input.GetProperty("steps").GetProperty("a").GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task Tool_step_completes_and_a_step_without_transitions_ends_the_run()
    {
        var def = Def([new StepDefinition(S("t"), StepKind.Tool, "echo", ["echo"])]);
        var tools = new FakeTools();
        var run = await Build(def, new FakeAgents(), tools).Start(input: Json("{}"));
        Assert.Equal(WorkflowRunStatus.Succeeded, run.Status);
        Assert.Equal(1, tools.Calls);
    }

    [Fact]
    public async Task Unknown_or_invalid_workflows_are_rejected_before_a_run_exists()
    {
        var h = Build(Def([Agent("a", "x")], T("nope", "ok")), new FakeAgents());
        await Assert.ThrowsAsync<InvalidWorkflowException>(async () => await h.Start());
        await Assert.ThrowsAsync<WorkflowNotFoundException>(async () =>
            await h.Orchestrator.StartAsync(new StartRunCommand(new WorkflowId("other")), default));
    }

    // ---- agents cannot steer the workflow

    [Fact]
    public async Task Outcome_outside_allowed_set_fails_the_step_and_the_run()
    {
        var agents = new FakeAgents().Add("agent-a", FakeAgent.Returning("agent-a", Ok("skip-ahead"))).Add("agent-b", FakeAgent.Returning("b", Ok("done")));
        var h = Build(TwoStep(), agents);
        var run = await h.Start();

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("outcome_not_allowed", run.Failure!.Code);
        Assert.Equal(FailureKind.Validation, run.Failure.Kind);
        var steps = await h.Store.GetStepRunsAsync(run.Id, default);
        Assert.Single(steps); // step b never ran
        Assert.Equal(StepRunStatus.Failed, steps[0].Status);
    }

    [Fact]
    public async Task Outcome_without_a_transition_fails_with_validation()
    {
        var def = Def([Agent("a", "agent-a"), Agent("b", "agent-b")], T("a", "ok", "b"));
        var agents = new FakeAgents().Add("agent-a", FakeAgent.Returning("a", Ok("surprise")));
        var run = await Build(def, agents).Start();
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("no_transition", run.Failure!.Code);
    }

    [Fact]
    public async Task Blank_outcome_and_missing_failure_are_engine_rejected()
    {
        var blank = new FakeAgents().Add("agent-a", FakeAgent.Returning("a", new AgentInvocationResult { Status = InvocationStatus.Succeeded }));
        Assert.Equal("missing_outcome", (await Build(TwoStep(), blank).Start()).Failure!.Code);

        var noFailure = new FakeAgents().Add("agent-a", FakeAgent.Returning("a", new AgentInvocationResult { Status = InvocationStatus.Failed }));
        Assert.Equal("missing_failure", (await Build(TwoStep(), noFailure).Start()).Failure!.Code);
    }

    [Fact]
    public async Task Agent_capabilities_not_declared_by_the_step_are_denied()
    {
        var sneaky = new FakeAgent("agent-a", (_, _) => ValueTask.FromResult(Ok("ok")), "write-repo");
        var def = Def([Agent("a", "agent-a", caps: ["read-repo"])]);
        var run = await Build(def, new FakeAgents().Add("agent-a", sneaky)).Start();

        Assert.Equal(FailureKind.PolicyDenied, run.Failure!.Kind);
        Assert.Empty(sneaky.Calls); // denied before invocation
    }

    [Fact]
    public async Task Tool_not_declared_as_a_capability_is_denied_without_calling_it()
    {
        var tools = new FakeTools();
        var def = Def([new StepDefinition(S("t"), StepKind.Tool, "echo")]);
        var run = await Build(def, new FakeAgents(), tools).Start(input: Json("{}"));
        Assert.Equal(FailureKind.PolicyDenied, run.Failure!.Kind);
        Assert.Equal(0, tools.Calls);
    }

    [Fact]
    public async Task Unregistered_agent_fails_the_run()
    {
        var run = await Build(TwoStep(), new FakeAgents()).Start();
        Assert.Equal("agent_not_found", run.Failure!.Code);
    }

    [Fact]
    public async Task Unexpected_exceptions_become_internal_failures_without_leaking_details()
    {
        var boom = new FakeAgent("agent-a", (_, _) => throw new InvalidOperationException("secret-token-123"));
        var run = await Build(TwoStep(), new FakeAgents().Add("agent-a", boom)).Start();
        Assert.Equal(FailureKind.Internal, run.Failure!.Kind);
        Assert.DoesNotContain("secret", run.Failure.Message);
    }

    [Fact]
    public async Task A_finished_run_cannot_be_driven_again()
    {
        var h = Build(TwoStep(), TwoAgents());
        var run = await h.Start();
        await Assert.ThrowsAsync<InvalidLifecycleTransitionException>(async () =>
            await h.Orchestrator.CancelAsync(run.Id, "late", default));
        await Assert.ThrowsAsync<InvalidLifecycleTransitionException>(async () =>
            await h.Orchestrator.ResumeAsync(ResumeRunCommand.WithInput(run.Id, Json("{}")), default));
        Assert.Equal(WorkflowRunStatus.Succeeded, (await h.Store.GetRunAsync(run.Id, default))!.Status);
    }

    // ---- failure & retry

    [Fact]
    public async Task Non_retryable_failure_fails_the_run_without_retry()
    {
        var agent = FakeAgent.Returning("agent-a", AgentInvocationResult.Fault(new Failure(FailureKind.Provider, "boom", "x", Retryable: false)));
        var def = Def([Agent("a", "agent-a", retry: new RetryPolicy(3))]);
        var run = await Build(def, new FakeAgents().Add("agent-a", agent)).Start();

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("boom", run.Failure!.Code);
        Assert.Single(agent.Calls);
    }

    [Fact]
    public async Task Retryable_failure_retries_as_new_attempts_then_succeeds()
    {
        var retryable = AgentInvocationResult.Fault(new Failure(FailureKind.Provider, "flaky", "x", Retryable: true));
        var agent = FakeAgent.Returning("agent-a", retryable, retryable, Ok("ok"));
        var def = Def([Agent("a", "agent-a", retry: new RetryPolicy(3))]);
        var h = Build(def, new FakeAgents().Add("agent-a", agent));
        var run = await h.Start();

        Assert.Equal(WorkflowRunStatus.Succeeded, run.Status);
        Assert.Equal([1, 2, 3], agent.Calls.Select(c => c.Context.Attempt));
        Assert.Equal(["run wf@1.0.0",
                      "  step a attempt=1 Failed failure=Provider/flaky",
                      "  step a attempt=2 Failed failure=Provider/flaky",
                      "  step a attempt=3 Succeeded outcome=ok",
                      "  => Succeeded"], await h.Trace(run));
    }

    [Fact]
    public async Task Retries_are_bounded_by_max_attempts()
    {
        var retryable = AgentInvocationResult.Fault(new Failure(FailureKind.Provider, "flaky", "x", Retryable: true));
        var agent = FakeAgent.Returning("agent-a", retryable);
        var def = Def([Agent("a", "agent-a", retry: new RetryPolicy(2))]);
        var run = await Build(def, new FakeAgents().Add("agent-a", agent)).Start();

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(2, agent.Calls.Count);
    }

    [Fact]
    public async Task Retryable_failure_without_a_retry_policy_is_not_retried()
    {
        var agent = FakeAgent.Returning("agent-a", AgentInvocationResult.Fault(new Failure(FailureKind.Provider, "flaky", "x", Retryable: true)));
        var run = await Build(Def([Agent("a", "agent-a")]), new FakeAgents().Add("agent-a", agent)).Start();
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Single(agent.Calls);
    }

    [Fact]
    public async Task Retry_delay_waits_on_the_injected_clock()
    {
        var time = new ManualTime();
        var retryable = AgentInvocationResult.Fault(new Failure(FailureKind.Provider, "flaky", "x", Retryable: true));
        var agent = FakeAgent.Returning("agent-a", retryable, Ok("ok"));
        var def = Def([Agent("a", "agent-a", retry: new RetryPolicy(2, TimeSpan.FromMinutes(5)))]);
        var pending = Build(def, new FakeAgents().Add("agent-a", agent), time: time).Start().AsTask();

        await WaitUntil(() => time.PendingTimers == 1);
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(WorkflowRunStatus.Succeeded, (await pending).Status);
    }

    [Fact]
    public async Task Timeout_produces_a_timeout_failure_and_is_retryable()
    {
        var time = new ManualTime();
        var agent = new FakeAgent("agent-a", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok("ok");
        });
        var def = Def([Agent("a", "agent-a", timeout: TimeSpan.FromSeconds(30))]);
        var pending = Build(def, new FakeAgents().Add("agent-a", agent), time: time).Start().AsTask();

        await WaitUntil(() => time.PendingTimers >= 1 && agent.Calls.Count == 1);
        time.Advance(TimeSpan.FromSeconds(30));
        var run = await pending;

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(FailureKind.Timeout, run.Failure!.Kind);
        Assert.Equal(time.GetUtcNow() , run.EndedAt);
    }

    [Fact]
    public async Task Step_budget_stops_runaway_rework_cycles()
    {
        var agent = FakeAgent.Returning("agent-a", Ok("again"));
        var def = Def([Agent("a", "agent-a", ["again"])], T("a", "again", "a"));
        var run = await Build(def, new FakeAgents().Add("agent-a", agent), options: new RuntimeOptions(MaxStepRuns: 5)).Start();

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("step_budget_exceeded", run.Failure!.Code);
        Assert.Equal(5, agent.Calls.Count);
    }

    // ---- cancellation

    [Fact]
    public async Task Token_cancelled_before_start_cancels_the_run_not_fails_it()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var agent = FakeAgent.Returning("agent-a", Ok("ok"));
        var run = await Build(TwoStep(), new FakeAgents().Add("agent-a", agent)).Start(cts.Token);

        Assert.Equal(WorkflowRunStatus.Cancelled, run.Status);
        Assert.Null(run.Failure);
        Assert.Empty(agent.Calls);
    }

    [Fact]
    public async Task Host_cancellation_during_a_step_cancels_step_and_run()
    {
        using var cts = new CancellationTokenSource();
        var agent = new FakeAgent("agent-a", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok("ok");
        });
        var h = Build(TwoStep(), new FakeAgents().Add("agent-a", agent));
        var pending = h.Start(cts.Token).AsTask();
        await WaitUntil(() => agent.Calls.Count == 1);
        cts.Cancel();
        var run = await pending;

        Assert.Equal(WorkflowRunStatus.Cancelled, run.Status);
        Assert.Null(run.Failure);
        var step = Assert.Single(await h.Store.GetStepRunsAsync(run.Id, default));
        Assert.Equal(StepRunStatus.Cancelled, step.Status);
    }

    [Fact]
    public async Task Agent_returning_cancelled_on_its_own_is_a_failure_not_a_run_cancellation()
    {
        var agent = FakeAgent.Returning("agent-a", AgentInvocationResult.CancelledResult());
        var run = await Build(TwoStep(), new FakeAgents().Add("agent-a", agent)).Start();
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("unrequested_cancellation", run.Failure!.Code);
    }

    // ---- waiting: input

    [Fact]
    public async Task Needs_input_pauses_and_resume_reinvokes_the_same_attempt_with_the_answer()
    {
        var agent = FakeAgent.Returning("agent-a", AgentInvocationResult.NeedsMoreInput(Json("[\"which repo?\"]")), Ok("ok"));
        var agents = new FakeAgents().Add("agent-a", agent).Add("agent-b", FakeAgent.Returning("b", Ok("done")));
        var h = Build(TwoStep(), agents);

        var paused = await h.Start();
        Assert.Equal(WorkflowRunStatus.Paused, paused.Status);
        var waiting = Assert.Single(await h.Store.GetStepRunsAsync(paused.Id, default));
        Assert.Equal(StepRunStatus.AwaitingInput, waiting.Status);
        Assert.Equal("which repo?", waiting.Output!.Value[0].GetString());

        var run = await h.Orchestrator.ResumeAsync(ResumeRunCommand.WithInput(paused.Id, Json("\"forge\"")), default);
        Assert.Equal(WorkflowRunStatus.Succeeded, run.Status);
        Assert.Equal("forge", agent.Calls[1].Input.GetProperty("answer").GetString());
        Assert.Equal(["run wf@1.0.0",
                      "  step a attempt=1 Succeeded outcome=ok",
                      "  step b attempt=1 Succeeded outcome=done",
                      "  => Succeeded"], await h.Trace(run));
    }

    [Fact]
    public async Task Resume_with_the_wrong_payload_is_rejected_and_leaves_the_run_paused()
    {
        var agent = FakeAgent.Returning("agent-a", AgentInvocationResult.NeedsMoreInput(Json("[]")));
        var h = Build(TwoStep(), new FakeAgents().Add("agent-a", agent));
        var paused = await h.Start();

        var decision = new ApprovalDecision("me", ApprovalDecisionKind.Approved, DateTimeOffset.UnixEpoch);
        await Assert.ThrowsAsync<InvalidLifecycleTransitionException>(async () =>
            await h.Orchestrator.ResumeAsync(ResumeRunCommand.WithDecision(paused.Id, decision), default));
        Assert.Equal(WorkflowRunStatus.Paused, (await h.Store.GetRunAsync(paused.Id, default))!.Status);
    }

    [Fact]
    public async Task Resuming_a_run_that_is_not_paused_is_rejected()
    {
        var gate = new TaskCompletionSource<AgentInvocationResult>();
        var agent = new FakeAgent("agent-a", (_, _) => new ValueTask<AgentInvocationResult>(gate.Task));
        var h = Build(TwoStep(), new FakeAgents().Add("agent-a", agent));
        var pending = h.Start().AsTask();
        await WaitUntil(() => agent.Calls.Count == 1);
        var running = (await h.Store.GetRunAsync(new WorkflowRunId("run-1"), default))!;
        Assert.Equal(WorkflowRunStatus.Running, running.Status);

        await Assert.ThrowsAsync<InvalidLifecycleTransitionException>(async () =>
            await h.Orchestrator.ResumeAsync(ResumeRunCommand.WithInput(running.Id, Json("{}")), default));
        gate.SetResult(Ok("ok"));
        await pending;
    }

    // ---- waiting: approval

    private static WorkflowDefinition ApprovalFlow() => Def(
        [Agent("draft", "agent-a", ["ok"]),
         new StepDefinition(S("gate"), StepKind.Approval, AllowedOutcomes: ["approved", "rejected"]),
         Agent("ship", "agent-b", ["done"])],
        T("draft", "ok", "gate"), T("gate", "approved", "ship"), T("gate", "rejected"), T("ship", "done"));

    [Theory]
    [InlineData(ApprovalDecisionKind.Approved, "approved", 3)]
    [InlineData(ApprovalDecisionKind.Rejected, "rejected", 2)]
    public async Task Approval_pauses_then_routes_on_the_decision(ApprovalDecisionKind kind, string expectedOutcome, int expectedSteps)
    {
        var h = Build(ApprovalFlow(), TwoAgents());
        var paused = await h.Start();
        Assert.Equal(WorkflowRunStatus.Paused, paused.Status);
        Assert.Equal("gate", paused.CurrentStep!.Value.Value);

        var run = await h.Orchestrator.ResumeAsync(await Decide(h.Store, paused.Id, "lead", kind, "because"), default);

        Assert.Equal(WorkflowRunStatus.Succeeded, run.Status);
        var steps = await h.Store.GetStepRunsAsync(run.Id, default);
        Assert.Equal(expectedSteps, steps.Count);
        var gate = steps.Single(s => s.StepId == S("gate"));
        Assert.Equal(expectedOutcome, gate.Outcome);
        Assert.Equal("lead", gate.Approval!.Approver); // decision kept for audit
    }

    [Fact]
    public async Task Approval_decision_with_no_transition_fails_the_run()
    {
        var h = Build(ApprovalFlow(), TwoAgents());
        var paused = await h.Start();
        var run = await h.Orchestrator.ResumeAsync(await Decide(h.Store, paused.Id, kind: ApprovalDecisionKind.ChangesRequested), default);

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("outcome_not_allowed", run.Failure!.Code);
    }

    [Fact]
    public async Task Approval_step_cannot_be_resumed_with_free_input()
    {
        var h = Build(ApprovalFlow(), TwoAgents());
        var paused = await h.Start();
        await Assert.ThrowsAsync<InvalidLifecycleTransitionException>(async () =>
            await h.Orchestrator.ResumeAsync(ResumeRunCommand.WithInput(paused.Id, Json("{}")), default));
    }

    [Fact]
    public async Task Cancelling_a_paused_run_cancels_its_waiting_step_run()
    {
        var h = Build(ApprovalFlow(), TwoAgents());
        var paused = await h.Start();
        var run = await h.Orchestrator.CancelAsync(paused.Id, "no longer needed", default);

        Assert.Equal(WorkflowRunStatus.Cancelled, run.Status);
        Assert.Equal("no longer needed", run.CancellationReason);
        var steps = await h.Store.GetStepRunsAsync(run.Id, default);
        Assert.Equal(StepRunStatus.Cancelled, steps.Single(s => s.StepId == S("gate")).Status);
        Assert.Equal(StepRunStatus.Succeeded, steps.Single(s => s.StepId == S("draft")).Status);
    }

    // ---- example trace (docs/design/execution-trace.md)

    [Fact]
    public async Task Spec_driven_delivery_example_trace()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "examples", "valid-spec-driven-delivery.json");
        var load = WorkflowLoader.LoadFile(path);
        Assert.True(load.IsValid, string.Join("; ", load.Errors));

        var review = FakeAgent.Returning("reviewer", Ok("changes-requested"), Ok("pass"));
        var agents = new FakeAgents()
            .Add("analyst", new FakeAgent("analyst", (_, _) => ValueTask.FromResult(Ok("ready")), "read-requirements"))
            .Add("implementer", new FakeAgent("implementer", (_, _) => ValueTask.FromResult(Ok("done")), "read-repo", "write-repo"))
            .Add("reviewer", new FakeAgent("reviewer", (r, _) => review.InvokeAsync(r, default), "read-repo"));
        var store = new InMemoryRunStore();
        var orch = new WorkflowOrchestrator(new FakeCatalog(load.Definition!), store, agents, new FakeTools(), new SequentialIds(), new ManualTime());

        var paused = await orch.StartAsync(new StartRunCommand(load.Definition!.Id), default);
        var run = await orch.ResumeAsync(await Decide(store, paused.Id), default);

        Assert.Equal(["run spec-driven-delivery@1.0.0",
                      "  step analyze attempt=1 Succeeded outcome=ready",
                      "  step approve-plan attempt=1 Succeeded outcome=approved",
                      "  step implement attempt=1 Succeeded outcome=done",
                      "  step review attempt=1 Succeeded outcome=changes-requested",
                      "  step implement attempt=1 Succeeded outcome=done",
                      "  step review attempt=1 Succeeded outcome=pass",
                      "  => Succeeded"],
            ExecutionTrace.Render(run, await store.GetStepRunsAsync(run.Id, default)));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Condition not reached in time.");
    }
}
