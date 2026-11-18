using System.Text.Json;
using AgenticForge.Abstractions;
using AgenticForge.Application;
using AgenticForge.Domain;
using AgenticForge.Runtime;
using static AgenticForge.Runtime.Tests.W;

namespace AgenticForge.Runtime.Tests;

public class EventTests
{
    private static AgentInvocationResult Ok(string outcome, string? output = null)
        => AgentInvocationResult.Success(outcome, output is null ? null : Json(output));

    private static FakeAgents TwoAgents() => new FakeAgents()
        .Add("agent-a", FakeAgent.Returning("agent-a", Ok("ok")))
        .Add("agent-b", FakeAgent.Returning("agent-b", Ok("done")));

    private static async Task<IReadOnlyList<ExecutionEvent>> Events(Harness h, WorkflowRunSnapshot run, EventChannel? channel = null)
        => await h.Events.GetEventsAsync(run.Id, channel, default);

    // ---- ordering & correlation

    [Fact]
    public async Task Events_are_sequenced_and_ordered_by_lifecycle()
    {
        var h = Build(TwoStep(), TwoAgents());
        var run = await h.Start();
        var events = await Events(h, run);

        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.Equal([ExecutionEventKind.RunStarted,
                      ExecutionEventKind.StepStarted, ExecutionEventKind.AgentInvoked, ExecutionEventKind.AgentCompleted, ExecutionEventKind.StepSucceeded,
                      ExecutionEventKind.StepStarted, ExecutionEventKind.AgentInvoked, ExecutionEventKind.AgentCompleted, ExecutionEventKind.StepSucceeded,
                      ExecutionEventKind.RunSucceeded], events.Select(e => e.Kind));
        Assert.Equal(events.Select(e => e.Timestamp).Order(), events.Select(e => e.Timestamp));
        Assert.Equal(events.Count, events.Select(e => e.EventId).Distinct().Count());
    }

    [Fact]
    public async Task Every_event_carries_run_correlation_and_workflow_version()
    {
        var h = Build(TwoStep(), TwoAgents());
        var run = await h.Start();
        var events = await Events(h, run);

        Assert.All(events, e =>
        {
            Assert.Equal(run.Id, e.RunId);
            Assert.Equal(run.CorrelationId, e.CorrelationId);
            Assert.Equal(new WorkflowVersion(1, 0, 0), e.WorkflowVersion);
            Assert.Equal(ContractSchema.Current, e.SchemaVersion);
        });
        var steps = await h.Store.GetStepRunsAsync(run.Id, default);
        // step-level events point at the persisted step runs; run-level events carry none
        Assert.Equal(steps.Select(s => s.Id), events.Where(e => e.Kind == ExecutionEventKind.StepSucceeded).Select(e => e.StepRunId!.Value));
        Assert.All(events.Where(e => e.Kind.ToString().StartsWith("Run")), e => Assert.Null(e.StepRunId));
        Assert.All(events.Where(e => e.Kind.ToString().StartsWith("Step")), e => Assert.NotNull(e.Attempt));
    }

    [Fact]
    public async Task Event_streams_of_different_runs_do_not_mix()
    {
        var h = Build(TwoStep(), TwoAgents());
        var r1 = await h.Start();
        var r2 = await h.Start();
        Assert.NotEqual(r1.Id, r2.Id);
        Assert.All(await Events(h, r1), e => Assert.Equal(r1.Id, e.RunId));
        Assert.Equal(1, (await Events(h, r2)).First().Sequence);
        Assert.Empty(await h.Events.GetEventsAsync(new WorkflowRunId("nope"), null, default));
    }

    [Fact]
    public async Task Audit_and_telemetry_are_separate_channels()
    {
        var h = Build(TwoStep(), TwoAgents());
        var run = await h.Start();
        var audit = await Events(h, run, EventChannel.Audit);
        var telemetry = await Events(h, run, EventChannel.Telemetry);

        Assert.Equal((await Events(h, run)).Count, audit.Count + telemetry.Count);
        Assert.All(telemetry, e => Assert.True(e.Kind is ExecutionEventKind.AgentInvoked or ExecutionEventKind.AgentCompleted));
        Assert.Contains(audit, e => e.Kind == ExecutionEventKind.RunSucceeded);
    }

    // ---- reconstruction

    [Fact]
    public async Task Run_can_be_reconstructed_from_events_alone_including_retry_and_failure()
    {
        var flaky = AgentInvocationResult.Fault(new Failure(FailureKind.Provider, "flaky", "x", Retryable: true));
        var agents = new FakeAgents().Add("agent-a", FakeAgent.Returning("agent-a", flaky, Ok("ok")))
                                     .Add("agent-b", FakeAgent.Returning("agent-b", AgentInvocationResult.Fault(new Failure(FailureKind.Provider, "boom", "x"))));
        var def = Def([Agent("a", "agent-a", ["ok"], retry: new RetryPolicy(2)), Agent("b", "agent-b", ["done"])],
            T("a", "ok", "b"), T("b", "done"));
        var h = Build(def, agents);
        var run = await h.Start();

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        var events = await Events(h, run);
        Assert.Contains(events, e => e.Kind == ExecutionEventKind.StepRetryScheduled && e.Failure!.Code == "flaky");
        Assert.Equal(await h.Trace(run), ExecutionTrace.RenderFromEvents(events));
        // audit events alone are enough
        Assert.Equal(await h.Trace(run), ExecutionTrace.RenderFromEvents(await Events(h, run, EventChannel.Audit)));
    }

    [Fact]
    public async Task Approval_flow_records_request_decision_actor_and_resume()
    {
        var h = Build(ApprovalFlow(), TwoAgents());
        var paused = await h.Start();
        var pausedKinds = (await Events(h, paused)).Select(e => e.Kind).ToList();
        Assert.Equal([ExecutionEventKind.ApprovalRequested, ExecutionEventKind.StepAwaitingApproval, ExecutionEventKind.RunPaused], pausedKinds.TakeLast(3));

        var run = await h.Orchestrator.ResumeAsync(ResumeRunCommand.WithDecision(paused.Id,
            new ApprovalDecision("lead", ApprovalDecisionKind.Approved, DateTimeOffset.UnixEpoch, "my private reason")), default);
        var events = await Events(h, run);

        var decided = events.Single(e => e.Kind == ExecutionEventKind.ApprovalDecided);
        Assert.Equal("lead", decided.Actor);
        Assert.Equal("approved", decided.Outcome);
        Assert.Equal(EventChannel.Audit, decided.Channel);
        Assert.DoesNotContain("my private reason", JsonSerializer.Serialize(events, ContractJson.Options)); // free text is not recorded
        Assert.True(events.Single(e => e.Kind == ExecutionEventKind.RunResumed).Sequence > decided.Sequence);
        Assert.Equal(await h.Trace(run), ExecutionTrace.RenderFromEvents(events));
    }

    [Fact]
    public async Task Cancellation_is_recorded_for_run_and_active_step()
    {
        var h = Build(ApprovalFlow(), TwoAgents());
        var paused = await h.Start();
        var run = await h.Orchestrator.CancelAsync(paused.Id, "stop", default);
        var events = await Events(h, run);

        Assert.Equal([ExecutionEventKind.StepCancelled, ExecutionEventKind.RunCancelled], events.TakeLast(2).Select(e => e.Kind));
        Assert.Equal(await h.Trace(run), ExecutionTrace.RenderFromEvents(events));
    }

    [Fact]
    public async Task Waiting_for_input_then_resuming_is_recorded()
    {
        var calls = 0;
        var agent = new FakeAgent("agent-a", (_, _) => ValueTask.FromResult(
            calls++ == 0 ? AgentInvocationResult.NeedsMoreInput(Json("{\"q\":1}")) : Ok("done")));
        var def = Def([Agent("a", "agent-a", ["done"])], T("a", "done"));
        var h = Build(def, new FakeAgents().Add("agent-a", agent));
        var paused = await h.Start();
        var run = await h.Orchestrator.ResumeAsync(ResumeRunCommand.WithInput(paused.Id, Json("\"yes\"")), default);

        var kinds = (await Events(h, run)).Select(e => e.Kind).ToList();
        Assert.Contains(ExecutionEventKind.StepAwaitingInput, kinds);
        Assert.Contains(ExecutionEventKind.RunResumed, kinds);
        Assert.Equal(ExecutionEventKind.RunSucceeded, kinds[^1]);
        Assert.Equal(await h.Trace(run), ExecutionTrace.RenderFromEvents(await Events(h, run)));
    }

    // ---- artifact references

    [Fact]
    public async Task Artifacts_are_recorded_as_references_with_digests_and_queryable_per_run()
    {
        var content = "# spec"u8.ToArray();
        var art = new ArtifactRef(new ArtifactId("art-1"), "file", "memory://artifacts/spec.md", "text/markdown", ArtifactDigest.Compute(content));
        var agents = new FakeAgents().Add("agent-a", FakeAgent.Returning("agent-a",
            new AgentInvocationResult { Status = InvocationStatus.Succeeded, Outcome = "ok", Artifacts = [art] }))
            .Add("agent-b", FakeAgent.Returning("agent-b", Ok("done")));
        var h = Build(TwoStep(), agents);
        var run = await h.Start();

        var events = await Events(h, run);
        Assert.Contains(events, e => e.Kind == ExecutionEventKind.AgentCompleted && e.Artifacts!.Single() == art);
        Assert.Contains(events, e => e.Kind == ExecutionEventKind.StepSucceeded && e.Artifacts!.Single() == art);
        var refs = await h.Events.GetArtifactsAsync(run.Id, default);
        Assert.Equal([art], refs); // de-duplicated across events
        Assert.True(ArtifactDigest.Verify(refs[0], content));
    }

    [Fact]
    public async Task Run_success_event_references_final_artifacts()
    {
        var art = new ArtifactRef(new ArtifactId("art-9"), "json", "memory://x", Digest: ArtifactDigest.Compute("{}"u8));
        var agents = new FakeAgents().Add("agent-a", FakeAgent.Returning("agent-a",
            new AgentInvocationResult { Status = InvocationStatus.Succeeded, Outcome = "done", Artifacts = [art] }));
        var def = Def([Agent("a", "agent-a", ["done"])], T("a", "done"));
        var h = Build(def, agents);
        var run = await h.Start();
        Assert.Equal([art], (await Events(h, run)).Single(e => e.Kind == ExecutionEventKind.RunSucceeded).Artifacts);
    }

    // ---- redaction & resilience

    [Fact]
    public async Task Secrets_in_failures_are_redacted_and_inputs_outputs_are_never_recorded()
    {
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var fail = AgentInvocationResult.Fault(new Failure(FailureKind.Provider, "auth", $"401 using token {token} and password=hunter2",
            Details: new Dictionary<string, string> { ["header"] = "Authorization: Bearer abcdef0123456789", ["api_key"] = "plain" }));
        var h = Build(TwoStep(), new FakeAgents().Add("agent-a", FakeAgent.Returning("agent-a", fail)));
        var run = await h.Start(input: Json("{\"prompt\":\"SENSITIVE-PROMPT\"}"));

        var json = JsonSerializer.Serialize(await Events(h, run), ContractJson.Options);
        foreach (var leaked in new[] { token, "hunter2", "abcdef0123456789", "plain", "SENSITIVE-PROMPT" })
            Assert.DoesNotContain(leaked, json);
        Assert.Contains(EventRedactor.Mask, json);
        Assert.Contains("auth", json); // the failure code stays for diagnosis
    }

    [Fact]
    public async Task A_throwing_sink_does_not_change_the_run_result()
    {
        var store = new InMemoryRunStore();
        var orch = new WorkflowOrchestrator(new FakeCatalog(TwoStep()), store, TwoAgents(), new FakeTools(), new SequentialIds(),
            new ManualTime(), events: new ThrowingSink());
        var run = await orch.StartAsync(new StartRunCommand(new WorkflowId("wf")), default);
        Assert.Equal(WorkflowRunStatus.Succeeded, run.Status);
    }

    [Fact]
    public async Task Events_serialize_to_versioned_json_and_round_trip()
    {
        var h = Build(TwoStep(), TwoAgents());
        var run = await h.Start();
        var first = (await Events(h, run))[0];
        var json = JsonSerializer.Serialize(first, ContractJson.Options);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"kind\": \"RunStarted\"", json);
        Assert.Contains("\"channel\": \"Audit\"", json);
        Assert.Equal(first, JsonSerializer.Deserialize<ExecutionEvent>(json, ContractJson.Options));
    }

    private static WorkflowDefinition ApprovalFlow() => Def(
        [Agent("draft", "agent-a", ["ok"]),
         new StepDefinition(S("gate"), StepKind.Approval, AllowedOutcomes: ["approved", "rejected"]),
         Agent("ship", "agent-b", ["done"])],
        T("draft", "ok", "gate"), T("gate", "approved", "ship"), T("gate", "rejected"), T("ship", "done"));

    private sealed class ThrowingSink : IExecutionEventSink
    {
        public ValueTask AppendAsync(ExecutionEvent e, CancellationToken ct) => throw new IOException("disk full");
    }
}

public class ArtifactDigestTests
{
    [Fact]
    public void Digest_is_sha256_hex_and_detects_tampering()
    {
        var content = "hello"u8.ToArray();
        var digest = ArtifactDigest.Compute(content);
        Assert.Equal("sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", digest);

        var art = new ArtifactRef(new ArtifactId("a"), "file", "memory://a", Digest: digest);
        Assert.True(ArtifactDigest.Verify(art, content));
        Assert.False(ArtifactDigest.Verify(art, "hellO"u8));
        Assert.False(ArtifactDigest.Verify(art with { Digest = null }, content)); // unverifiable is not verified
        Assert.Equal(digest, ArtifactDigest.Compute(new MemoryStream(content)));
    }
}

public class EventRedactorTests
{
    [Theory]
    [InlineData("token=abc123xyz", "abc123xyz")]
    [InlineData("{\"password\": \"s3cr3t\"}", "s3cr3t")]
    [InlineData("Authorization: Bearer abcdefgh12345678", "abcdefgh12345678")]
    [InlineData("key sk-abcdefghijklmnopqrstuv here", "sk-abcdefghijklmnopqrstuv")]
    [InlineData("aws AKIAABCDEFGHIJKLMNOP", "AKIAABCDEFGHIJKLMNOP")]
    public void Known_secret_shapes_are_masked(string text, string secret)
    {
        var redacted = EventRedactor.Redact(text);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains(EventRedactor.Mask, redacted);
    }

    [Fact]
    public void Ordinary_text_is_kept_and_long_text_is_truncated()
    {
        Assert.Equal("Step 'a' exceeded its timeout.", EventRedactor.Redact("Step 'a' exceeded its timeout."));
        Assert.True(EventRedactor.Redact(new string('x', 5000)).Length <= EventRedactor.MaxTextLength + 3);
    }
}
