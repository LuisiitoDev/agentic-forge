using System.Text;
using AgenticForge.Application;
using AgenticForge.Domain;
using AgenticForge.Persistence.Sqlite;
using AgenticForge.Runtime;
using AgenticForge.Samples.EngineeringWorkflow;

namespace AgenticForge.Samples.Tests;

public sealed class EngineeringWorkflowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("forge-sample-").FullName;
    private string Db => Path.Combine(_dir, "runs.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static ValueTask<EngineeringHost> Host(SampleOptions? options = null, IRunStore? store = null)
        => EngineeringHost.CreateAsync(store ?? new InMemoryRunStore(), options);

    private static ValueTask<WorkflowRunSnapshot> Start(EngineeringHost h) => h.StartAsync("Add dark mode", "Users want it.", SampleRunner.ExternalReference);

    // ---------------------------------------------------------------- definition

    [Fact]
    public void Definition_is_valid_acyclic_and_free_of_vendor_terms()
    {
        var def = EngineeringHost.LoadDefinition();
        Assert.Empty(def.Validate());
        Assert.Equal(["plan", "approve-plan", "implement", "review", "rework", "final-review", "escalate", "publish"],
            def.Steps.Select(s => s.Id.Value));

        // Rework is bounded by construction: the graph has no cycle, so no step can run twice.
        Assert.False(HasCycle(def));

        using var stream = typeof(EngineeringHost).Assembly.GetManifestResourceStream("engineering-workflow.json")!;
        var text = new StreamReader(stream).ReadToEnd().ToLowerInvariant();
        foreach (var vendor in new[] { "github", "gitlab", "jira", "claude", "openai", "issue", "pull" })
            Assert.DoesNotContain(vendor, text);
    }

    private static bool HasCycle(WorkflowDefinition def)
    {
        var state = new Dictionary<StepId, int>();
        bool Visit(StepId id)
        {
            if (state.GetValueOrDefault(id) == 1) return true;
            if (state.GetValueOrDefault(id) == 2) return false;
            state[id] = 1;
            foreach (var t in def.Transitions.Where(t => t.From == id && t.To is not null))
                if (Visit(t.To!.Value)) return true;
            state[id] = 2;
            return false;
        }
        return def.Steps.Any(s => Visit(s.Id));
    }

    // ---------------------------------------------------------------- approval pause / resume

    [Fact]
    public async Task Run_pauses_at_plan_approval_and_does_no_implementation_until_approved()
    {
        await using var h = await Host();
        var run = await Start(h);

        Assert.Equal(WorkflowRunStatus.Paused, run.Status);
        Assert.Equal(new StepId("approve-plan"), run.CurrentStep);
        Assert.Equal(1, h.Planner.Calls);
        Assert.Equal(0, h.Implementer.Calls);
        Assert.Equal(0, h.Publisher.Effects);
        Assert.Equal(SampleRunner.ExternalReference, run.CorrelationId);
    }

    [Fact]
    public async Task Invalid_approvals_are_denied_and_leave_the_run_paused_then_a_valid_one_resumes_it()
    {
        await using var h = await Host();
        var run = await Start(h);

        // Wrong digest (stale/forged), unauthorized human, and an automated identity (an agent id) are all refused.
        var wrongDigest = await Assert.ThrowsAsync<ApprovalDeniedException>(async () => await h.DecideAsync(run.Id, EngineeringHost.Approver, digest: "sha256:deadbeef"));
        Assert.Equal("approval_digest_mismatch", wrongDigest.Decision.Code);
        var stranger = await Assert.ThrowsAsync<ApprovalDeniedException>(async () => await h.DecideAsync(run.Id, "mallory"));
        Assert.Equal("approver_not_authorized", stranger.Decision.Code);
        await Assert.ThrowsAsync<ApprovalDeniedException>(async () => await h.DecideAsync(run.Id, "reviewer"));

        Assert.Equal(WorkflowRunStatus.Paused, (await h.Store.GetRunAsync(run.Id, default))!.Status);
        Assert.Equal(0, h.Implementer.Calls);
        Assert.Equal(3, (await h.Events.GetEventsAsync(run.Id, EventChannel.Audit, default)).Count(e => e.Kind == ExecutionEventKind.PolicyDecided));

        var done = await h.DecideAsync(run.Id, EngineeringHost.Approver);
        Assert.Equal(WorkflowRunStatus.Succeeded, done.Status);
        Assert.True(h.Implementer.Calls > 0);
    }

    [Fact]
    public async Task Rejecting_the_plan_ends_the_run_without_implementing_or_publishing()
    {
        await using var h = await Host();
        var run = await Start(h);

        var done = await h.DecideAsync(run.Id, EngineeringHost.Approver, ApprovalDecisionKind.Rejected, "not now");

        Assert.Equal(WorkflowRunStatus.Succeeded, done.Status);
        Assert.Equal(["run engineering-workflow@1.0.0", "  step plan attempt=1 Succeeded outcome=planned",
            "  step approve-plan attempt=1 Succeeded outcome=rejected", "  => Succeeded"], await h.TraceAsync(run.Id));
        Assert.Equal(0, h.Implementer.Calls);
        Assert.Equal(0, h.Publisher.Effects);
    }

    // ---------------------------------------------------------------- rework

    [Fact]
    public async Task Reviewer_finding_causes_exactly_one_rework_and_feeds_the_finding_to_the_implementer()
    {
        await using var h = await Host(new SampleOptions(ReviewRoundsRequestingChanges: 1));
        var run = await h.DecideAsync((await Start(h)).Id, EngineeringHost.Approver);

        Assert.Equal(WorkflowRunStatus.Succeeded, run.Status);
        Assert.Equal(EngineeringTrace.OneRework, await h.TraceAsync(run.Id));
        Assert.Equal(["Missing test for edge case (round 1)"], h.Implementer.AddressedFindings);
        Assert.Equal(2, h.Reviewer.Calls);
        Assert.Equal(1, h.Publisher.Effects);
    }

    [Fact]
    public async Task Clean_first_review_skips_rework()
    {
        await using var h = await Host(new SampleOptions(ReviewRoundsRequestingChanges: 0));
        var run = await h.DecideAsync((await Start(h)).Id, EngineeringHost.Approver);

        Assert.Equal(["run engineering-workflow@1.0.0", "  step plan attempt=1 Succeeded outcome=planned",
            "  step approve-plan attempt=1 Succeeded outcome=approved", "  step implement attempt=1 Succeeded outcome=implemented",
            "  step review attempt=1 Succeeded outcome=pass", "  step publish attempt=1 Succeeded outcome=completed", "  => Succeeded"],
            await h.TraceAsync(run.Id));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(50)]
    public async Task A_reviewer_that_never_relents_cannot_loop_the_run_it_escalates_to_a_human_after_one_rework(int rounds)
    {
        await using var h = await Host(new SampleOptions(rounds));
        var run = await h.DecideAsync((await Start(h)).Id, EngineeringHost.Approver);

        Assert.Equal(WorkflowRunStatus.Paused, run.Status);
        Assert.Equal(new StepId("escalate"), run.CurrentStep);
        Assert.Equal(2, h.Implementer.Calls); // implement + one rework, never a third
        Assert.Equal(2, h.Reviewer.Calls);
        Assert.Equal(0, h.Publisher.Effects);

        var done = await h.DecideAsync(run.Id, EngineeringHost.Approver);
        Assert.Equal(WorkflowRunStatus.Succeeded, done.Status);
        Assert.Equal(EngineeringTrace.Escalated, await h.TraceAsync(run.Id));
        Assert.Equal(1, h.Publisher.Effects);
    }

    [Fact]
    public async Task Human_can_reject_the_escalation_and_nothing_is_published()
    {
        await using var h = await Host(new SampleOptions(2));
        var run = await h.DecideAsync((await Start(h)).Id, EngineeringHost.Approver);

        var done = await h.DecideAsync(run.Id, EngineeringHost.Approver, ApprovalDecisionKind.Rejected, "still not good enough");

        Assert.Equal(WorkflowRunStatus.Succeeded, done.Status);
        Assert.Equal("  step escalate attempt=1 Succeeded outcome=rejected", (await h.TraceAsync(run.Id))[^2]);
        Assert.Equal(0, h.Publisher.Effects);
    }

    [Fact]
    public async Task Runtime_budget_is_a_second_guard_even_if_a_definition_loops()
    {
        // A looser definition that loops review -> implement forever; only the run budget stops it.
        var shipped = EngineeringHost.LoadDefinition();
        string[] dropped = ["rework", "final-review", "escalate"];
        var looping = shipped with
        {
            Steps = [.. shipped.Steps.Where(st => !dropped.Contains(st.Id.Value))],
            Transitions = [.. shipped.Transitions.Where(t => !dropped.Contains(t.From.Value) && t.Outcome != "changes-requested"),
                new Transition(new StepId("review"), "changes-requested", new StepId("implement"))],
        };
        await using var h = await Host(new SampleOptions(1000, looping, new RuntimeOptions(MaxStepRuns: 12)));

        var run = await h.DecideAsync((await Start(h)).Id, EngineeringHost.Approver);

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("step_budget_exceeded", run.Failure!.Code);
        Assert.Equal(12, (await h.Store.GetStepRunsAsync(run.Id, default)).Count);
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public async Task Run_survives_a_restart_while_waiting_for_approval()
    {
        WorkflowRunId id;
        await using (var first = await Host(store: new SqliteRunStore(Db)))
        {
            var paused = await Start(first);
            id = paused.Id;
            Assert.Equal(WorkflowRunStatus.Paused, paused.Status);
        }

        await using var second = await Host(store: new SqliteRunStore(Db));
        Assert.Equal(WorkflowRunStatus.Paused, (await second.Store.GetRunAsync(id, default))!.Status);
        Assert.NotNull(await second.PendingApprovalAsync(id));

        var done = await second.DecideAsync(id, EngineeringHost.Approver);

        Assert.Equal(WorkflowRunStatus.Succeeded, done.Status);
        Assert.Equal(0, second.Planner.Calls); // the finished plan step was not executed again
        Assert.Equal(EngineeringTrace.OneRework, await second.TraceAsync(id));
    }

    [Fact]
    public async Task Redelivered_request_returns_the_same_run_and_does_not_re_plan()
    {
        await using var h = await Host();
        var a = await h.StartAsync("t", "b", SampleRunner.ExternalReference, "request-42");
        var b = await h.StartAsync("t", "b", SampleRunner.ExternalReference, "request-42");

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(1, h.Planner.Calls);
    }

    // ---------------------------------------------------------------- traceability

    [Fact]
    public async Task Every_transition_and_artifact_is_traceable_from_the_audit_history()
    {
        await using var h = await Host(new SampleOptions(1));
        var run = await h.DecideAsync((await Start(h)).Id, EngineeringHost.Approver);

        var audit = await h.Events.GetEventsAsync(run.Id, EventChannel.Audit, default);
        Assert.Equal(Enumerable.Range(1, audit.Count).Select(_ => true), audit.Select(e => e.Sequence > 0)); // sequenced

        // The trace can be rebuilt from audit events alone and matches the stored-state trace.
        Assert.Equal(await h.TraceAsync(run.Id), ExecutionTrace.RenderFromEvents(audit));

        // The approval is attributed and bound.
        var decided = Assert.Single(audit, e => e.Kind == ExecutionEventKind.ApprovalDecided);
        Assert.Equal(EngineeringHost.Approver, decided.Actor);
        Assert.Equal((await h.Store.GetStepRunsAsync(run.Id, default)).Single(s => s.StepId == new StepId("approve-plan")).ApprovalRequest!.Binding.Digest,
            decided.Attributes!["bindingDigest"]);

        // Checkpoints record every outcome and the step it routed to.
        var cps = (await h.Store.GetCheckpointsAsync(run.Id, default)).Where(c => c.Kind == CheckpointKind.StepCompleted).ToList();
        Assert.Equal(["plan", "approve-plan", "implement", "review", "rework", "final-review", "publish"], cps.Select(c => c.Step!.Value.Value));
        Assert.Equal("rework", cps.Single(c => c.Step == new StepId("review")).NextStep!.Value.Value);

        // Every artifact the steps produced is in the history with a digest that matches its content.
        var history = await h.Events.GetArtifactsAsync(run.Id, default);
        var fromSteps = (await h.Store.GetStepRunsAsync(run.Id, default)).SelectMany(s => s.Artifacts ?? []).ToList();
        Assert.Equal(fromSteps.Select(a => a.Id), history.Select(a => a.Id));
        Assert.Equal(["plan", "patch-r1", "review-r1", "patch-r2", "review-r2", "change-request-1"], history.Select(a => a.Id.Value));
        Assert.All(history, a => Assert.StartsWith("sha256:", a.Digest));
        Assert.Contains(history, a => a.Id.Value == "plan" && ArtifactDigest.Verify(a, Encoding.UTF8.GetBytes("# Plan: Add dark mode\n1. Add the change\n2. Cover it with tests\n")));
    }

    // ---------------------------------------------------------------- runnable host

    [Theory]
    [InlineData(1, ApprovalDecisionKind.Approved, "outcome=implemented\n  step final-review attempt=1 Succeeded outcome=pass")]
    [InlineData(2, ApprovalDecisionKind.Approved, "step escalate attempt=1 Succeeded outcome=approved")]
    [InlineData(2, ApprovalDecisionKind.Rejected, "step escalate attempt=1 Succeeded outcome=rejected")]
    public async Task Sample_host_runs_end_to_end_without_any_external_service(int rounds, ApprovalDecisionKind finalDecision, string expected)
    {
        var writer = new StringWriter();
        var run = await SampleRunner.RunAsync(writer, Db, rounds, finalDecision);

        Assert.Equal(WorkflowRunStatus.Succeeded, run.Status);
        var text = writer.ToString().Replace("\r\n", "\n");
        Assert.Contains("[process 2] restarted", text);
        Assert.Contains("approver_not_authorized", text);
        Assert.Contains(expected, text);
    }

    [Fact]
    public async Task Documented_execution_trace_matches_the_real_one()
    {
        var doc = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "design", "engineering-workflow-sample.md")).Replace("\r\n", "\n");
        await using var h = await Host(new SampleOptions(1));
        var run = await h.DecideAsync((await Start(h)).Id, EngineeringHost.Approver);
        Assert.Contains(string.Join("\n", await h.TraceAsync(run.Id)), doc);

        await using var e = await Host(new SampleOptions(2));
        var atEscalation = await e.DecideAsync((await Start(e)).Id, EngineeringHost.Approver);
        var escalated = await e.DecideAsync(atEscalation.Id, EngineeringHost.Approver);
        Assert.Contains(string.Join("\n", await e.TraceAsync(escalated.Id)), doc);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgenticForge.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

internal static class EngineeringTrace
{
    public static readonly string[] OneRework =
    [
        "run engineering-workflow@1.0.0",
        "  step plan attempt=1 Succeeded outcome=planned",
        "  step approve-plan attempt=1 Succeeded outcome=approved",
        "  step implement attempt=1 Succeeded outcome=implemented",
        "  step review attempt=1 Succeeded outcome=changes-requested",
        "  step rework attempt=1 Succeeded outcome=implemented",
        "  step final-review attempt=1 Succeeded outcome=pass",
        "  step publish attempt=1 Succeeded outcome=completed",
        "  => Succeeded",
    ];

    public static readonly string[] Escalated =
    [
        "run engineering-workflow@1.0.0",
        "  step plan attempt=1 Succeeded outcome=planned",
        "  step approve-plan attempt=1 Succeeded outcome=approved",
        "  step implement attempt=1 Succeeded outcome=implemented",
        "  step review attempt=1 Succeeded outcome=changes-requested",
        "  step rework attempt=1 Succeeded outcome=implemented",
        "  step final-review attempt=1 Succeeded outcome=changes-requested",
        "  step escalate attempt=1 Succeeded outcome=approved",
        "  step publish attempt=1 Succeeded outcome=completed",
        "  => Succeeded",
    ];
}
