using System.Text;
using AgenticForge.Abstractions;
using AgenticForge.Application;
using AgenticForge.Domain;
using AgenticForge.Runtime;
using static AgenticForge.Runtime.Tests.W;

namespace AgenticForge.Runtime.Tests;

public class GovernanceTests
{
    private static ArtifactRef Plan(string content = "plan v1")
        => new(new ArtifactId("plan"), "text", "mem://plan", "text/plain", ArtifactDigest.Compute(Encoding.UTF8.GetBytes(content)));

    private static AgentInvocationResult Ok(string outcome, params ArtifactRef[] artifacts)
        => AgentInvocationResult.Success(outcome, Json("{\"n\":1}"), artifacts.Length == 0 ? null : artifacts);

    private static FakeAgents Agents(ArtifactRef? plan = null, params string[] declaredCaps)
        => new FakeAgents()
            .Add("planner", new FakeAgent("planner", (_, _) => ValueTask.FromResult(plan is null ? Ok("ok") : Ok("ok", plan)), declaredCaps))
            .Add("worker", FakeAgent.Returning("worker", Ok("ok")));

    private static StepDefinition Gate() => new(S("gate"), StepKind.Approval, AllowedOutcomes: ["approved", "rejected"]);
    private static StepDefinition Deploy() => new(S("deploy"), StepKind.Tool, "echo", ["echo"]);

    /// <summary>planner -> gate -> (approved) deploy tool.</summary>
    private static WorkflowDefinition GatedDeploy() => Def(
        [Agent("plan", "planner", ["ok"]), Gate(), Deploy()],
        T("plan", "ok", "gate"), T("gate", "approved", "deploy"), T("gate", "rejected"));

    private static readonly GovernanceOptions GateEcho = new(ApprovalRequiredTools: new HashSet<string> { "echo" });

    private static Harness Build(WorkflowDefinition def, FakeAgents agents, FakeTools? tools = null, GovernanceOptions? gov = null)
    {
        var store = new InMemoryRunStore();
        var events = new InMemoryEventLog();
        var orch = new WorkflowOrchestrator(new FakeCatalog(def), store, agents, tools ?? new FakeTools(), new SequentialIds(),
            new ManualTime(), null, events, gov);
        return new Harness(orch, store, events);
    }

    private static async Task<IReadOnlyList<ExecutionEvent>> Events(Harness h, WorkflowRunSnapshot run)
        => await h.Events.GetEventsAsync(run.Id, null, default);

    private static async Task<StepRunSnapshot> Waiting(Harness h, WorkflowRunSnapshot run)
        => (await h.Store.GetStepRunsAsync(run.Id, default)).Single(s => s.Status == StepRunStatus.AwaitingApproval);

    private static ResumeRunCommand Resume(WorkflowRunSnapshot run, string approver, string? digest,
        ApprovalDecisionKind kind = ApprovalDecisionKind.Approved)
        => ResumeRunCommand.WithDecision(run.Id, new ApprovalDecision(approver, kind, DateTimeOffset.UnixEpoch, null, digest));

    // ---------------------------------------------------------------- approval binding

    [Fact]
    public async Task Approval_request_is_bound_to_run_step_version_artifact_and_context()
    {
        var h = Build(GatedDeploy(), Agents(Plan()));
        var paused = await h.Start();
        var request = (await Waiting(h, paused)).ApprovalRequest!;

        Assert.Equal(ApprovalStatus.Pending, request.Status);
        Assert.Equal(paused.Id, request.Binding.Run);
        Assert.Equal(S("gate"), request.Binding.Step);
        Assert.Equal(new WorkflowVersion(1, 0, 0), request.Binding.Version);
        Assert.Equal(ApprovalBinding.SubjectDigestOf([Plan()]), request.Binding.SubjectDigest);
        Assert.StartsWith("sha256:", request.Binding.Digest);

        var requested = (await Events(h, paused)).Single(e => e.Kind == ExecutionEventKind.ApprovalRequested);
        Assert.Equal(request.Binding.Digest, requested.Attributes!["bindingDigest"]);
    }

    [Fact]
    public void Binding_digest_changes_when_any_bound_value_changes()
    {
        var a = new ApprovalBinding(new WorkflowRunId("r"), S("gate"), new StepRunId("s"), new WorkflowId("wf"), new WorkflowVersion(1, 0, 0),
            ApprovalBinding.SubjectDigestOf([Plan("v1")])!, "sha256:ctx");
        Assert.NotEqual(a.Digest, (a with { SubjectDigest = ApprovalBinding.SubjectDigestOf([Plan("v2")])! }).Digest);
        Assert.NotEqual(a.Digest, (a with { ContextDigest = "sha256:other" }).Digest);
        Assert.NotEqual(a.Digest, (a with { Run = new WorkflowRunId("r2") }).Digest);
        Assert.NotEqual(a.Digest, (a with { Step = S("other") }).Digest);
        Assert.NotEqual(a.Digest, (a with { Version = new WorkflowVersion(1, 0, 1) }).Digest);
        Assert.Null(ApprovalBinding.SubjectDigestOf([Plan() with { Digest = null }]));
    }

    [Fact]
    public async Task Approval_with_the_matching_digest_is_recorded_with_who_what_and_when()
    {
        var h = Build(GatedDeploy(), Agents(Plan()), gov: GateEcho);
        var paused = await h.Start();
        var digest = (await Waiting(h, paused)).ApprovalRequest!.Binding.Digest;

        var run = await h.Orchestrator.ResumeAsync(Resume(paused, "lead", digest), default);

        Assert.Equal(WorkflowRunStatus.Succeeded, run.Status);
        var gate = (await h.Store.GetStepRunsAsync(run.Id, default)).Single(s => s.StepId == S("gate"));
        Assert.Equal(ApprovalStatus.Approved, gate.ApprovalRequest!.Status);
        Assert.Equal("lead", gate.ApprovalRequest.DecidedBy);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero), gate.ApprovalRequest.DecidedAt); // engine clock, not caller's
        var decided = (await Events(h, run)).Single(e => e.Kind == ExecutionEventKind.ApprovalDecided);
        Assert.Equal(digest, decided.Attributes!["bindingDigest"]);
        Assert.Equal(EventChannel.Audit, decided.Channel);
    }

    // ---------------------------------------------------------------- denied approvals

    [Theory]
    [InlineData(null, "approval_digest_missing")]
    [InlineData("sha256:deadbeef", "approval_digest_mismatch")]
    public async Task Approval_without_or_with_the_wrong_digest_is_denied_audited_and_leaves_the_run_paused(string? digest, string code)
    {
        var h = Build(GatedDeploy(), Agents(Plan()));
        var paused = await h.Start();

        var ex = await Assert.ThrowsAsync<ApprovalDeniedException>(async () => await h.Orchestrator.ResumeAsync(Resume(paused, "lead", digest), default));

        Assert.Equal(code, ex.Decision.Code);
        Assert.Equal(WorkflowRunStatus.Paused, (await h.Store.GetRunAsync(paused.Id, default))!.Status);
        Assert.Equal(ApprovalStatus.Pending, (await Waiting(h, paused)).ApprovalRequest!.Status);
        var denial = (await Events(h, paused)).Single(e => e.Kind == ExecutionEventKind.PolicyDecided);
        Assert.Equal(EventChannel.Audit, denial.Channel);
        Assert.Equal("Deny", denial.Attributes!["decision"]);
        Assert.Equal(code, denial.Attributes["code"]);
        Assert.Equal("lead", denial.Actor);

        // the correct decision still works afterwards
        var good = await h.Orchestrator.ResumeAsync(Resume(paused, "lead", (await Waiting(h, paused)).ApprovalRequest!.Binding.Digest), default);
        Assert.Equal(WorkflowRunStatus.Succeeded, good.Status);
    }

    [Fact]
    public async Task An_agent_or_tool_identity_cannot_approve()
    {
        var h = Build(GatedDeploy(), Agents(Plan()));
        var paused = await h.Start();
        var digest = (await Waiting(h, paused)).ApprovalRequest!.Binding.Digest;

        foreach (var self in new[] { "planner", "worker", "echo" })
        {
            var ex = await Assert.ThrowsAsync<ApprovalDeniedException>(async () => await h.Orchestrator.ResumeAsync(Resume(paused, self, digest), default));
            Assert.Equal("approver_not_authorized", ex.Decision.Code);
        }
        Assert.Equal(WorkflowRunStatus.Paused, (await h.Store.GetRunAsync(paused.Id, default))!.Status);
    }

    [Fact]
    public async Task Only_configured_approvers_may_decide_when_an_allow_list_is_set()
    {
        var h = Build(GatedDeploy(), Agents(Plan()), gov: new GovernanceOptions(Approvers: new HashSet<string> { "lead" }));
        var paused = await h.Start();
        var digest = (await Waiting(h, paused)).ApprovalRequest!.Binding.Digest;

        var ex = await Assert.ThrowsAsync<ApprovalDeniedException>(async () => await h.Orchestrator.ResumeAsync(Resume(paused, "mallory", digest), default));
        Assert.Equal("approver_not_authorized", ex.Decision.Code);
        Assert.Equal(WorkflowRunStatus.Succeeded, (await h.Orchestrator.ResumeAsync(Resume(paused, "lead", digest), default)).Status);
    }

    [Fact]
    public async Task A_blank_approver_is_denied()
    {
        var h = Build(GatedDeploy(), Agents(Plan()));
        var paused = await h.Start();
        var digest = (await Waiting(h, paused)).ApprovalRequest!.Binding.Digest;
        var ex = await Assert.ThrowsAsync<ApprovalDeniedException>(async () => await h.Orchestrator.ResumeAsync(Resume(paused, " ", digest), default));
        Assert.Equal("approver_not_authorized", ex.Decision.Code);
    }

    [Fact]
    public async Task A_waiting_step_without_an_approval_request_denies_by_default()
    {
        var h = Build(GatedDeploy(), Agents(Plan()));
        var paused = await h.Start();
        var step = await Waiting(h, paused);
        await h.Store.SaveAsync(step with { ApprovalRequest = null }, default);

        var ex = await Assert.ThrowsAsync<ApprovalDeniedException>(async () => await h.Orchestrator.ResumeAsync(Resume(paused, "lead", "sha256:x"), default));
        Assert.Equal("approval_request_missing", ex.Decision.Code);
    }

    [Fact]
    public async Task Rejecting_needs_no_digest_but_still_needs_an_authorized_approver()
    {
        var h = Build(GatedDeploy(), Agents(Plan()));
        var paused = await h.Start();
        await Assert.ThrowsAsync<ApprovalDeniedException>(async () =>
            await h.Orchestrator.ResumeAsync(Resume(paused, "planner", null, ApprovalDecisionKind.Rejected), default));

        var run = await h.Orchestrator.ResumeAsync(Resume(paused, "lead", null, ApprovalDecisionKind.Rejected), default);
        Assert.Equal(WorkflowRunStatus.Succeeded, run.Status); // 'rejected' is a terminal transition in this workflow
        Assert.Equal(ApprovalStatus.Rejected, (await h.Store.GetStepRunsAsync(run.Id, default)).Single(s => s.StepId == S("gate")).ApprovalRequest!.Status);
    }

    // ---------------------------------------------------------------- invalidation

    [Fact]
    public async Task Changing_the_approved_artifact_invalidates_the_approval_and_fails_the_run()
    {
        var h = Build(GatedDeploy(), Agents(Plan()), gov: GateEcho);
        var tools = new FakeTools();
        var paused = await h.Start();
        var request = (await Waiting(h, paused)).ApprovalRequest!;

        // the plan artifact is swapped for different content after the approver was shown the request
        var draft = (await h.Store.GetStepRunsAsync(paused.Id, default)).Single(s => s.StepId == S("plan"));
        await h.Store.SaveAsync(draft with { Artifacts = [Plan("plan v2 (tampered)")] }, default);

        var run = await h.Orchestrator.ResumeAsync(Resume(paused, "lead", request.Binding.Digest), default);

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("approval_invalidated", run.Failure!.Code);
        Assert.Equal(FailureKind.PolicyDenied, run.Failure.Kind);
        var gate = (await h.Store.GetStepRunsAsync(run.Id, default)).Single(s => s.StepId == S("gate"));
        Assert.Equal(ApprovalStatus.Invalidated, gate.ApprovalRequest!.Status);
        Assert.Equal(StepRunStatus.Failed, gate.Status);
        Assert.Equal(0, tools.Calls);
        var events = await Events(h, run);
        var inv = events.Single(e => e.Kind == ExecutionEventKind.ApprovalInvalidated);
        Assert.Equal(EventChannel.Audit, inv.Channel);
        Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKind.ApprovalDecided);
        Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKind.ToolInvoked);
    }

    [Fact]
    public async Task An_artifact_without_a_digest_cannot_be_approved()
    {
        var h = Build(GatedDeploy(), Agents(Plan() with { Digest = null }));
        var run = await h.Start();

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("approval_unbindable", run.Failure!.Code);
        Assert.DoesNotContain(await Events(h, run), e => e.Kind == ExecutionEventKind.ApprovalRequested);
    }

    // ---------------------------------------------------------------- approval-required tools

    [Fact]
    public async Task Approval_required_tool_is_denied_when_no_approval_exists()
    {
        var tools = new FakeTools();
        var def = Def([Agent("plan", "planner", ["ok"]), Deploy()], T("plan", "ok", "deploy"));
        var h = Build(def, Agents(Plan()), tools, GateEcho);
        var run = await h.Start();

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("approval_required", run.Failure!.Code);
        Assert.Equal(FailureKind.PolicyDenied, run.Failure.Kind);
        Assert.Equal(0, tools.Calls);
        var policy = (await Events(h, run)).Single(e => e.Kind == ExecutionEventKind.PolicyDecided);
        Assert.Equal("Deny", policy.Attributes!["decision"]);
        Assert.Equal("echo", policy.Attributes["target"]);
    }

    [Fact]
    public async Task Approval_required_tool_runs_after_a_valid_approval_and_the_allow_is_audited()
    {
        var tools = new FakeTools();
        var h = Build(GatedDeploy(), Agents(Plan()), tools, GateEcho);
        var paused = await h.Start();
        var run = await h.Orchestrator.ResumeAsync(Resume(paused, "lead", (await Waiting(h, paused)).ApprovalRequest!.Binding.Digest), default);

        Assert.Equal(WorkflowRunStatus.Succeeded, run.Status);
        Assert.Equal(1, tools.Calls);
        var policy = (await Events(h, run)).Single(e => e.Kind == ExecutionEventKind.PolicyDecided);
        Assert.Equal("Allow", policy.Attributes!["decision"]);
        Assert.Equal(EventChannel.Audit, policy.Channel);
    }

    [Fact]
    public async Task Approval_does_not_cover_material_that_changed_after_it_was_granted()
    {
        // plan -> gate -> worker (produces new output, changing the context) -> deploy
        var tools = new FakeTools();
        var def = Def([Agent("plan", "planner", ["ok"]), Gate(), Agent("work", "worker", ["ok"]), Deploy()],
            T("plan", "ok", "gate"), T("gate", "approved", "work"), T("gate", "rejected"), T("work", "ok", "deploy"));
        var h = Build(def, Agents(Plan()), tools, GateEcho);
        var paused = await h.Start();
        var run = await h.Orchestrator.ResumeAsync(Resume(paused, "lead", (await Waiting(h, paused)).ApprovalRequest!.Binding.Digest), default);

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("approval_stale", run.Failure!.Code);
        Assert.Equal(0, tools.Calls);
    }

    [Fact]
    public async Task An_approval_from_another_run_does_not_authorize_this_run()
    {
        var tools = new FakeTools();
        var h = Build(GatedDeploy(), Agents(Plan()), tools, GateEcho);
        var first = await h.Start();
        await h.Orchestrator.ResumeAsync(Resume(first, "lead", (await Waiting(h, first)).ApprovalRequest!.Binding.Digest), default);

        var direct = Build(Def([Agent("plan", "planner", ["ok"]), Deploy()], T("plan", "ok", "deploy")), Agents(Plan()), tools, GateEcho);
        var run = await direct.Start();
        Assert.Equal("approval_required", run.Failure!.Code);
    }

    // ---------------------------------------------------------------- capabilities

    [Fact]
    public async Task Agent_needing_an_undeclared_capability_is_denied_and_never_invoked()
    {
        var agent = new FakeAgent("planner", (_, _) => ValueTask.FromResult(Ok("ok")), "write-repo");
        var h = Build(Def([Agent("plan", "planner", ["ok"], caps: ["read-repo"])], T("plan", "ok")), new FakeAgents().Add("planner", agent));
        var run = await h.Start();

        Assert.Equal("capability_denied", run.Failure!.Code);
        Assert.Equal(FailureKind.PolicyDenied, run.Failure.Kind);
        Assert.Empty(agent.Calls);
        Assert.Contains(await Events(h, run), e => e.Kind == ExecutionEventKind.PolicyDecided && e.Attributes!["code"] == "capability_denied");
    }

    [Fact]
    public async Task Agent_output_cannot_grant_capabilities_to_later_steps()
    {
        var tools = new FakeTools();
        var claim = new FakeAgent("planner", (_, _) => ValueTask.FromResult(
            AgentInvocationResult.Success("ok", Json("{\"capabilities\":[\"echo\"],\"approved\":true,\"grant\":\"echo\"}"))));
        var def = Def([Agent("plan", "planner", ["ok"]), new StepDefinition(S("deploy"), StepKind.Tool, "echo", [])],
            T("plan", "ok", "deploy"));
        var h = Build(def, new FakeAgents().Add("planner", claim), tools);
        var run = await h.Start();

        Assert.Equal("capability_denied", run.Failure!.Code);
        Assert.Equal(0, tools.Calls);
    }

    [Fact]
    public async Task Host_ceiling_denies_capabilities_a_workflow_declares_but_the_host_did_not_grant()
    {
        var tools = new FakeTools();
        var def = Def([new StepDefinition(S("t"), StepKind.Tool, "echo", ["echo"])]);
        var denied = await Build(def, Agents(), tools, new GovernanceOptions(AllowedCapabilities: new HashSet<string> { "read-repo" })).Start();
        Assert.Equal("capability_not_granted", denied.Failure!.Code);
        Assert.Equal(0, tools.Calls);

        var allowed = await Build(def, Agents(), tools, new GovernanceOptions(AllowedCapabilities: new HashSet<string> { "echo" })).Start();
        Assert.Equal(WorkflowRunStatus.Succeeded, allowed.Status);
        Assert.Equal(1, tools.Calls);
    }

    // ---------------------------------------------------------------- evaluator is pure and deny-by-default

    [Fact]
    public void Evaluator_denies_unknown_actions_and_missing_decisions()
    {
        var e = new DefaultPolicyEvaluator();
        var baseRequest = new PolicyRequest(new WorkflowRunId("r"), new WorkflowId("wf"), new WorkflowVersion(1, 0, 0), S("s"),
            PolicyAction.Approve, "s", [], [], "lead");
        Assert.Equal("approval_decision_missing", e.Evaluate(baseRequest).Code);
        Assert.False(e.Evaluate(baseRequest with { Action = (PolicyAction)99 }).Allowed);
        Assert.False(e.Evaluate(baseRequest with { Decision = ApprovalDecisionKind.Approved }).Allowed); // no digest
        Assert.True(e.Evaluate(baseRequest with { Decision = ApprovalDecisionKind.Approved, PresentedDigest = "sha256:a", ExpectedDigest = "sha256:a" }).Allowed);
    }

    [Fact]
    public void Approval_request_lifecycle_is_one_way()
    {
        var b = new ApprovalBinding(new WorkflowRunId("r"), S("g"), null, new WorkflowId("wf"), new WorkflowVersion(1, 0, 0), "sha256:a", "sha256:b");
        var now = DateTimeOffset.UnixEpoch;
        var pending = ApprovalRequest.Request(b, now);
        var approved = pending.Decide("lead", ApprovalDecisionKind.Approved, now);

        Assert.Throws<InvalidLifecycleTransitionException>(() => approved.Decide("lead", ApprovalDecisionKind.Rejected, now));
        var invalid = approved.Invalidate(now, "changed");
        Assert.Equal(ApprovalStatus.Invalidated, invalid.Status);
        Assert.Throws<InvalidLifecycleTransitionException>(() => invalid.Decide("lead", ApprovalDecisionKind.Approved, now));
        Assert.Throws<InvalidLifecycleTransitionException>(() => invalid.Invalidate(now, "again"));
    }
}
