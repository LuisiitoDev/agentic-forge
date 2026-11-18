using AgenticForge.Abstractions;
using AgenticForge.Application;
using AgenticForge.Domain;
using AgenticForge.Persistence.Sqlite;
using AgenticForge.Runtime;
using AgenticForge.Runtime.Tests;
using Microsoft.Data.Sqlite;
using static AgenticForge.Runtime.Tests.W;

namespace AgenticForge.Persistence.Sqlite.Tests;

/// <summary>Stands in for the process dying: lets a save reach the disk, then throws so the engine never continues.</summary>
internal sealed class SimulatedCrash : Exception;

internal sealed class CrashingStore(IRunStore inner, Func<object, bool> crashAfterSaving) : IRunStore
{
    private int _armed = 1;

    private T Maybe<T>(T saved) where T : class
    {
        if (crashAfterSaving(saved) && Interlocked.Exchange(ref _armed, 0) == 1) throw new SimulatedCrash();
        return saved;
    }

    public async ValueTask<WorkflowRunSnapshot> SaveAsync(WorkflowRunSnapshot run, CancellationToken ct) => Maybe(await inner.SaveAsync(run, ct));
    public async ValueTask<StepRunSnapshot> SaveAsync(StepRunSnapshot stepRun, CancellationToken ct) => Maybe(await inner.SaveAsync(stepRun, ct));
    public ValueTask<WorkflowRunSnapshot?> GetRunAsync(WorkflowRunId id, CancellationToken ct) => inner.GetRunAsync(id, ct);
    public ValueTask<IReadOnlyList<StepRunSnapshot>> GetStepRunsAsync(WorkflowRunId id, CancellationToken ct) => inner.GetStepRunsAsync(id, ct);
    public ValueTask<WorkflowRunSnapshot?> FindRunByIdempotencyKeyAsync(WorkflowId w, string key, CancellationToken ct) => inner.FindRunByIdempotencyKeyAsync(w, key, ct);
    public ValueTask<IReadOnlyList<WorkflowRunSnapshot>> ListRunsAsync(IReadOnlyCollection<WorkflowRunStatus> s, CancellationToken ct) => inner.ListRunsAsync(s, ct);
    public ValueTask<RunCheckpoint> AppendCheckpointAsync(RunCheckpoint c, CancellationToken ct) => inner.AppendCheckpointAsync(c, ct);
    public ValueTask<IReadOnlyList<RunCheckpoint>> GetCheckpointsAsync(WorkflowRunId id, CancellationToken ct) => inner.GetCheckpointsAsync(id, ct);
}

/// <summary>
/// "Process" = one orchestrator over one store opened on the database file. A restart is a new Process on the same file, with
/// fresh agents (call counts start at zero) and no memory of the previous one.
/// </summary>
internal sealed class Process
{
    public FakeAgent A { get; }
    public FakeAgent B { get; }
    public IRunStore Store { get; }
    public WorkflowOrchestrator Orchestrator { get; }
    public InMemoryEventLog Events { get; } = new();

    public Process(string dbPath, WorkflowDefinition def, Func<object, bool>? crashAfterSaving = null, Func<AgentInvocationRequest, bool>? bHangs = null)
    {
        A = FakeAgent.Returning("agent-a", AgentInvocationResult.Success("ok", Json("{\"n\":1}")));
        B = new FakeAgent("agent-b", (r, _) => ValueTask.FromResult(AgentInvocationResult.Success("done", Json("{\"n\":2}"))));
        var sqlite = new SqliteRunStore(dbPath);
        Store = crashAfterSaving is null ? sqlite : new CrashingStore(sqlite, crashAfterSaving);
        Orchestrator = new WorkflowOrchestrator(new FakeCatalog(def), Store,
            new FakeAgents().Add("agent-a", A).Add("agent-b", B), new FakeTools(), new GuidIdGenerator(), new ManualTime(), null, Events);
    }

    public ValueTask<WorkflowRunSnapshot> Start(string? key = null)
        => Orchestrator.StartAsync(new StartRunCommand(new WorkflowId("wf"), Json("{\"issue\":1}"), IdempotencyKey: key), default);

    public async Task<IReadOnlyList<string>> Trace(WorkflowRunId id)
        => ExecutionTrace.Render((await Store.GetRunAsync(id, default))!, await Store.GetStepRunsAsync(id, default));
}

public class RecoveryTests : IDisposable
{
    private readonly TempDb _db = new();
    public void Dispose() => _db.Dispose();

    private Process Boot(WorkflowDefinition def, Func<object, bool>? crash = null)
    {
        SqliteConnection.ClearAllPools(); // a new process shares nothing but the file
        return new Process(_db.Path, def, crash);
    }

    private static bool RunSaved(object o, WorkflowRunStatus status, string? step = null)
        => o is WorkflowRunSnapshot r && r.Status == status && (step is null || r.CurrentStep?.Value == step);

    private static bool StepSaved(object o, string step, StepRunStatus status)
        => o is StepRunSnapshot s && s.StepId.Value == step && s.Status == status;

    private static WorkflowDefinition ApprovalFlow() => Def(
        [Agent("draft", "agent-a", ["ok"]),
         new StepDefinition(S("gate"), StepKind.Approval, AllowedOutcomes: ["approved", "rejected"]),
         Agent("ship", "agent-b", ["done"])],
        T("draft", "ok", "gate"), T("gate", "approved", "ship"), T("gate", "rejected"), T("ship", "done"));

    private static readonly ApprovalDecision Approve = new("lead", ApprovalDecisionKind.Approved, DateTimeOffset.UnixEpoch, "ship it");

    // ---- waiting workflows survive a restart

    [Fact]
    public async Task Run_waiting_for_approval_is_restored_after_restart_and_completes_without_rerunning_finished_steps()
    {
        var before = Boot(ApprovalFlow());
        var paused = await before.Start();
        Assert.Equal(WorkflowRunStatus.Paused, paused.Status);
        Assert.Single(before.A.Calls);

        var after = Boot(ApprovalFlow()); // restart
        var restored = (await after.Store.GetRunAsync(paused.Id, default))!;
        Assert.Equal(WorkflowRunStatus.Paused, restored.Status);
        Assert.Equal("gate", restored.CurrentStep!.Value.Value);

        var recovery = await after.Orchestrator.RecoverAllAsync(default);
        Assert.Empty(recovery); // waiting is not interrupted

        var done = await after.Orchestrator.ResumeAsync(ResumeRunCommand.WithDecision(paused.Id, Approve), default);
        Assert.Equal(WorkflowRunStatus.Succeeded, done.Status);
        Assert.Empty(after.A.Calls);   // 'draft' ran before the restart and was not repeated
        Assert.Single(after.B.Calls);
        Assert.Equal(["run wf@1.0.0",
                      "  step draft attempt=1 Succeeded outcome=ok",
                      "  step gate attempt=1 Succeeded outcome=approved",
                      "  step ship attempt=1 Succeeded outcome=done",
                      "  => Succeeded"], await after.Trace(paused.Id));
    }

    [Fact]
    public async Task Run_waiting_for_input_is_restored_and_resumes_after_restart()
    {
        var def = Def([Agent("a", "agent-a", ["ok"])], T("a", "ok"));
        var before = Boot(def);
        var asker = FakeAgent.Returning("agent-a", AgentInvocationResult.NeedsMoreInput(Json("{\"q\":\"which?\"}")));
        var orch = new WorkflowOrchestrator(new FakeCatalog(def), before.Store, new FakeAgents().Add("agent-a", asker), new FakeTools(), new GuidIdGenerator(), new ManualTime());
        var paused = await orch.StartAsync(new StartRunCommand(new WorkflowId("wf")), default);
        Assert.Equal(WorkflowRunStatus.Paused, paused.Status);

        var after = Boot(def);
        var done = await after.Orchestrator.ResumeAsync(ResumeRunCommand.WithInput(paused.Id, Json("{\"pick\":2}")), default);
        Assert.Equal(WorkflowRunStatus.Succeeded, done.Status);
        Assert.Equal(2, after.A.Calls.Single().Input.GetProperty("answer").GetProperty("pick").GetInt32());
    }

    // ---- interrupted runs

    [Fact]
    public async Task Crash_after_a_step_finished_but_before_the_run_advanced_does_not_rerun_that_step()
    {
        var before = Boot(TwoStep(), crash: o => StepSaved(o, "a", StepRunStatus.Succeeded));
        await Assert.ThrowsAsync<SimulatedCrash>(async () => await before.Start());
        Assert.Single(before.A.Calls);

        var after = Boot(TwoStep());
        var interrupted = Assert.Single(await after.Store.ListRunsAsync([WorkflowRunStatus.Running], default));
        var result = Assert.Single(await after.Orchestrator.RecoverAllAsync(default));

        Assert.Equal(RecoveryOutcome.Recovered, result.Outcome);
        Assert.Equal(WorkflowRunStatus.Succeeded, result.Snapshot!.Status);
        Assert.Equal(1, result.Snapshot.RecoveryCount);
        Assert.Empty(after.A.Calls);       // 'a' had already succeeded
        Assert.Single(after.B.Calls);
        Assert.Equal(["run wf@1.0.0",
                      "  step a attempt=1 Succeeded outcome=ok",
                      "  step b attempt=1 Succeeded outcome=done",
                      "  => Succeeded"], await after.Trace(interrupted.Id));
        // b received a's output even though a ran in another process
        Assert.Equal(1, after.B.Calls.Single().Input.GetProperty("steps").GetProperty("a").GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task Crash_after_the_run_moved_to_the_next_step_starts_that_step_fresh()
    {
        var before = Boot(TwoStep(), crash: o => RunSaved(o, WorkflowRunStatus.Running, "b"));
        await Assert.ThrowsAsync<SimulatedCrash>(async () => await before.Start());

        var after = Boot(TwoStep());
        var result = Assert.Single(await after.Orchestrator.RecoverAllAsync(default));
        Assert.Equal(WorkflowRunStatus.Succeeded, result.Snapshot!.Status);
        Assert.Empty(after.A.Calls);
        Assert.Single(after.B.Calls);
    }

    [Fact]
    public async Task Step_interrupted_mid_execution_fails_the_run_unless_the_workflow_declares_a_retry()
    {
        var before = Boot(TwoStep(), crash: o => StepSaved(o, "b", StepRunStatus.Running));
        await Assert.ThrowsAsync<SimulatedCrash>(async () => await before.Start());

        var after = Boot(TwoStep());
        var result = Assert.Single(await after.Orchestrator.RecoverAllAsync(default));

        Assert.Equal(WorkflowRunStatus.Failed, result.Snapshot!.Status);
        Assert.Equal(WorkflowOrchestrator.InterruptedFailureCode, result.Snapshot.Failure!.Code);
        Assert.Empty(after.A.Calls);
        Assert.Empty(after.B.Calls); // effects of the interrupted step are unknown, so it is not silently repeated
        var steps = await after.Store.GetStepRunsAsync(result.Run, default);
        Assert.Equal([StepRunStatus.Succeeded, StepRunStatus.Failed], steps.Select(s => s.Status));
    }

    [Fact]
    public async Task Interrupted_step_is_retried_as_a_new_attempt_when_the_workflow_declares_retry()
    {
        var def = Def([Agent("a", "agent-a", ["ok"]), Agent("b", "agent-b", ["done"], retry: new RetryPolicy(2))],
            T("a", "ok", "b"), T("b", "done"));
        var before = Boot(def, crash: o => StepSaved(o, "b", StepRunStatus.Running));
        await Assert.ThrowsAsync<SimulatedCrash>(async () => await before.Start());

        var after = Boot(def);
        var result = Assert.Single(await after.Orchestrator.RecoverAllAsync(default));

        Assert.Equal(WorkflowRunStatus.Succeeded, result.Snapshot!.Status);
        Assert.Empty(after.A.Calls);
        Assert.Single(after.B.Calls);
        Assert.Equal(["run wf@1.0.0",
                      "  step a attempt=1 Succeeded outcome=ok",
                      "  step b attempt=1 Failed failure=Internal/interrupted_by_restart",
                      "  step b attempt=2 Succeeded outcome=done",
                      "  => Succeeded"], await after.Trace(result.Run));
    }

    [Fact]
    public async Task Run_created_but_never_started_is_started_by_recovery()
    {
        var before = Boot(TwoStep(), crash: o => RunSaved(o, WorkflowRunStatus.Pending));
        await Assert.ThrowsAsync<SimulatedCrash>(async () => await before.Start());
        Assert.Empty(before.A.Calls);

        var after = Boot(TwoStep());
        var result = Assert.Single(await after.Orchestrator.RecoverAllAsync(default));
        Assert.Equal(WorkflowRunStatus.Succeeded, result.Snapshot!.Status);
        Assert.Single(after.A.Calls);
    }

    [Fact]
    public async Task Crash_while_applying_an_approval_is_completed_by_recovery_without_repeating_work()
    {
        var before = Boot(ApprovalFlow());
        var paused = await before.Start();
        // Crash after the step run was marked Succeeded but before the run itself left Paused.
        var crashing = Boot(ApprovalFlow(), crash: o => StepSaved(o, "gate", StepRunStatus.Succeeded));
        await Assert.ThrowsAsync<SimulatedCrash>(async () =>
            await crashing.Orchestrator.ResumeAsync(ResumeRunCommand.WithDecision(paused.Id, Approve), default));

        var after = Boot(ApprovalFlow());
        Assert.Equal(WorkflowRunStatus.Paused, (await after.Store.GetRunAsync(paused.Id, default))!.Status);
        await Assert.ThrowsAsync<InvalidLifecycleTransitionException>(async () =>
            await after.Orchestrator.ResumeAsync(ResumeRunCommand.WithDecision(paused.Id, Approve), default)); // no longer waiting

        var result = Assert.Single(await after.Orchestrator.RecoverAllAsync(default));
        Assert.Equal(WorkflowRunStatus.Succeeded, result.Snapshot!.Status);
        Assert.Empty(after.A.Calls);
        Assert.Single(after.B.Calls);
    }

    [Fact]
    public async Task Recover_all_handles_a_mix_and_skips_terminal_and_waiting_runs()
    {
        var finished = await Boot(TwoStep()).Start();
        var waiting = await Boot(ApprovalFlow()).Start();
        var crashed = Boot(TwoStep(), crash: o => StepSaved(o, "a", StepRunStatus.Succeeded));
        await Assert.ThrowsAsync<SimulatedCrash>(async () => await crashed.Start());

        var after = Boot(TwoStep());
        var results = await after.Orchestrator.RecoverAllAsync(default);

        var only = Assert.Single(results);
        Assert.Equal(RecoveryOutcome.Recovered, only.Outcome);
        Assert.NotEqual(finished.Id, only.Run);
        Assert.NotEqual(waiting.Id, only.Run);
        Assert.Equal(WorkflowRunStatus.Paused, (await after.Store.GetRunAsync(waiting.Id, default))!.Status);
        Assert.Equal(0, (await after.Store.GetRunAsync(finished.Id, default))!.RecoveryCount);
        Assert.Empty(await after.Orchestrator.RecoverAllAsync(default)); // idempotent: nothing left to recover
    }

    [Fact]
    public async Task Recovery_of_a_missing_workflow_version_is_reported_and_does_not_stop_other_runs()
    {
        var crashed = Boot(TwoStep(), crash: o => StepSaved(o, "a", StepRunStatus.Succeeded));
        await Assert.ThrowsAsync<SimulatedCrash>(async () => await crashed.Start());

        var other = Def([Agent("a", "agent-a", ["ok"])], T("a", "ok")) with { Id = new WorkflowId("different") };
        var after = Boot(other);
        var result = Assert.Single(await after.Orchestrator.RecoverAllAsync(default));
        Assert.Equal(RecoveryOutcome.Failed, result.Outcome);
        Assert.Contains("was not found", result.Detail);
        Assert.Equal(WorkflowRunStatus.Running, (await after.Store.GetRunAsync(result.Run, default))!.Status); // left for a later attempt
    }

    // ---- checkpoints & events

    [Fact]
    public async Task Checkpoints_are_durable_ordered_and_recovery_appends_its_own()
    {
        var crashed = Boot(TwoStep(), crash: o => StepSaved(o, "b", StepRunStatus.Running));
        await Assert.ThrowsAsync<SimulatedCrash>(async () => await crashed.Start());

        var after = Boot(TwoStep());
        var run = Assert.Single(await after.Store.ListRunsAsync([WorkflowRunStatus.Running], default));
        var beforeRecovery = await after.Store.GetCheckpointsAsync(run.Id, default);
        var first = Assert.Single(beforeRecovery);
        Assert.Equal(CheckpointKind.StepCompleted, first.Kind);
        Assert.Equal("a", first.Step!.Value.Value);
        Assert.Equal("b", first.NextStep!.Value.Value);
        Assert.Equal("ok", first.Outcome);

        await after.Orchestrator.RecoverAsync(run.Id, default);
        var all = await after.Store.GetCheckpointsAsync(run.Id, default);
        Assert.Equal([1L, 2L], all.Select(c => c.Sequence).Take(2));
        Assert.Equal(CheckpointKind.Recovered, all[1].Kind);
    }

    [Fact]
    public async Task Pausing_writes_a_waiting_checkpoint_and_recovery_emits_an_audit_event()
    {
        var p = Boot(ApprovalFlow());
        var paused = await p.Start();
        var cps = await p.Store.GetCheckpointsAsync(paused.Id, default);
        Assert.Equal([CheckpointKind.StepCompleted, CheckpointKind.Waiting], cps.Select(c => c.Kind));

        var crashed = Boot(TwoStep(), crash: o => StepSaved(o, "a", StepRunStatus.Succeeded));
        await Assert.ThrowsAsync<SimulatedCrash>(async () => await crashed.Start());
        var after = Boot(TwoStep());
        var result = Assert.Single(await after.Orchestrator.RecoverAllAsync(default));
        var events = await after.Events.GetEventsAsync(result.Run, null, default);
        var recovered = Assert.Single(events, e => e.Kind == ExecutionEventKind.RunRecovered);
        Assert.Equal(EventChannel.Audit, recovered.Channel);
        Assert.Equal("Running", recovered.Attributes!["previousStatus"]);
    }

    // ---- duplicate requests

    [Fact]
    public async Task Duplicate_start_with_the_same_key_returns_the_same_run_even_across_restarts()
    {
        var first = Boot(TwoStep());
        var run = await first.Start("issue-42");
        Assert.Equal(WorkflowRunStatus.Succeeded, run.Status);

        var second = Boot(TwoStep()); // restart, then the trigger fires again
        var again = await second.Start("issue-42");

        Assert.Equal(run.Id, again.Id);
        Assert.Equal(run.Revision, again.Revision); // untouched
        Assert.Equal(run.Status, again.Status);
        Assert.Empty(second.A.Calls);
        Assert.Empty(second.B.Calls);
        Assert.Single(await second.Store.ListRunsAsync([WorkflowRunStatus.Succeeded], default));
    }

    [Fact]
    public async Task Different_or_missing_keys_start_separate_runs()
    {
        var p = Boot(TwoStep());
        var a = await p.Start("k1");
        var b = await p.Start("k2");
        var c = await p.Start();
        var d = await p.Start();
        Assert.Equal(4, new[] { a.Id, b.Id, c.Id, d.Id }.Distinct().Count());
    }

    [Fact]
    public async Task Racing_duplicate_starts_execute_the_workflow_once()
    {
        var def = ApprovalFlow(); // pauses, so every racer sees an in-flight run
        var processes = Enumerable.Range(0, 6).Select(_ => Boot(def)).ToArray();
        var results = await Task.WhenAll(processes.Select(p => Task.Run(async () => await p.Start("same-trigger"))));

        Assert.Single(results.Select(r => r.Id).Distinct());
        Assert.Equal(1, processes.Sum(p => p.A.Calls.Count));
        Assert.Single(await processes[0].Store.ListRunsAsync([WorkflowRunStatus.Paused, WorkflowRunStatus.Running, WorkflowRunStatus.Pending], default));
    }

    // ---- competing workers

    [Fact]
    public async Task Competing_recoverers_continue_an_interrupted_run_only_once()
    {
        var crashed = Boot(TwoStep(), crash: o => StepSaved(o, "a", StepRunStatus.Succeeded));
        await Assert.ThrowsAsync<SimulatedCrash>(async () => await crashed.Start());

        var workers = Enumerable.Range(0, 4).Select(_ => Boot(TwoStep())).ToArray();
        var runId = Assert.Single(await workers[0].Store.ListRunsAsync([WorkflowRunStatus.Running], default)).Id;
        var results = await Task.WhenAll(workers.Select(w => Task.Run(async () => await w.Orchestrator.RecoverAsync(runId, default).AsTask())));

        Assert.Single(results, r => r.Outcome == RecoveryOutcome.Recovered);
        Assert.All(results.Where(r => r.Outcome != RecoveryOutcome.Recovered),
            r => Assert.True(r.Outcome is RecoveryOutcome.ClaimedElsewhere or RecoveryOutcome.NotInterrupted));
        Assert.Equal(1, workers.Sum(w => w.B.Calls.Count)); // the last step ran exactly once across all workers
        Assert.Equal(0, workers.Sum(w => w.A.Calls.Count));
    }

    [Fact]
    public async Task Cancel_racing_with_a_resume_cannot_silently_overwrite_the_other()
    {
        var p = Boot(ApprovalFlow());
        var paused = await p.Start();
        var w1 = Boot(ApprovalFlow());
        var w2 = Boot(ApprovalFlow());

        var outcomes = await Task.WhenAll(
            Task.Run(async () => await Try(() => w1.Orchestrator.ResumeAsync(ResumeRunCommand.WithDecision(paused.Id, Approve), default).AsTask())),
            Task.Run(async () => await Try(() => w2.Orchestrator.CancelAsync(paused.Id, "stop", default).AsTask())));

        var final = (await p.Store.GetRunAsync(paused.Id, default))!;
        Assert.True(final.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Cancelled, final.Status.ToString());
        Assert.Contains(outcomes, ok => ok); // one side won; the other either also fit in order or was rejected, never merged
        // A cancelled run never later turns into a success because of a stale writer.
        var again = (await p.Store.GetRunAsync(paused.Id, default))!;
        Assert.Equal(final.Status, again.Status);

        static async Task<bool> Try(Func<Task> action)
        {
            try { await action(); return true; }
            catch (Exception e) when (e is RunConcurrencyException or InvalidLifecycleTransitionException) { return false; }
        }
    }
}
