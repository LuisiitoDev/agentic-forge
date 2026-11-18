using AgenticForge.Application;
using AgenticForge.Domain;
using AgenticForge.Persistence.Sqlite;
using AgenticForge.Runtime;
using static AgenticForge.Runtime.Tests.W;

namespace AgenticForge.Persistence.Sqlite.Tests;

/// <summary>Behavior every <see cref="IRunStore"/> must have: compare-and-set revisions, idempotency keys, ordering, checkpoints.</summary>
public abstract class RunStoreContractTests : IDisposable
{
    protected abstract IRunStore CreateStore();
    public virtual void Dispose() { }

    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static WorkflowRunSnapshot Run(string id, string? key = null, string workflow = "wf")
        => new(new WorkflowRunId(id), new WorkflowId(workflow), new WorkflowVersion(1, 0, 0), "corr", WorkflowRunStatus.Pending, T0,
            IdempotencyKey: key);

    private static StepRunSnapshot Step(string id, string run, StepRunStatus status = StepRunStatus.Pending)
        => new(new StepRunId(id), new WorkflowRunId(run), new StepId("a"), 1, status);

    [Fact]
    public async Task Save_assigns_revisions_starting_at_one_and_round_trips()
    {
        var store = CreateStore();
        var saved = await store.SaveAsync(Run("r1") with { Input = Json("{\"x\":1}") }, default);
        Assert.Equal(1, saved.Revision);
        var again = await store.SaveAsync(saved with { Status = WorkflowRunStatus.Running }, default);
        Assert.Equal(2, again.Revision);

        var loaded = (await store.GetRunAsync(new WorkflowRunId("r1"), default))!;
        Assert.Equal(WorkflowRunStatus.Running, loaded.Status);
        Assert.Equal(2, loaded.Revision);
        Assert.Equal(1, loaded.Input!.Value.GetProperty("x").GetInt32());
        Assert.Null(await store.GetRunAsync(new WorkflowRunId("missing"), default));
    }

    [Fact]
    public async Task Stale_run_save_is_rejected_and_does_not_overwrite_newer_state()
    {
        var store = CreateStore();
        var v1 = await store.SaveAsync(Run("r1"), default);
        await store.SaveAsync(v1 with { Status = WorkflowRunStatus.Running }, default); // someone else wins

        var ex = await Assert.ThrowsAsync<RunConcurrencyException>(async () =>
            await store.SaveAsync(v1 with { Status = WorkflowRunStatus.Cancelled }, default));
        Assert.Equal(1, ex.ExpectedRevision);
        Assert.Equal(2, ex.ActualRevision);
        Assert.Equal(WorkflowRunStatus.Running, (await store.GetRunAsync(v1.Id, default))!.Status);
    }

    [Fact]
    public async Task Inserting_an_existing_run_as_new_is_a_conflict()
    {
        var store = CreateStore();
        await store.SaveAsync(Run("r1"), default);
        await Assert.ThrowsAsync<RunConcurrencyException>(async () => await store.SaveAsync(Run("r1"), default));
    }

    [Fact]
    public async Task Updating_a_run_that_was_never_stored_is_a_conflict()
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<RunConcurrencyException>(async () => await store.SaveAsync(Run("ghost") with { Revision = 3 }, default));
    }

    [Fact]
    public async Task Stale_step_save_is_rejected_and_steps_keep_insertion_order()
    {
        var store = CreateStore();
        var s1 = await store.SaveAsync(Step("s1", "r1"), default);
        await store.SaveAsync(Step("s2", "r1"), default);
        var s1b = await store.SaveAsync(s1 with { Status = StepRunStatus.Running }, default);
        await Assert.ThrowsAsync<RunConcurrencyException>(async () => await store.SaveAsync(s1 with { Status = StepRunStatus.Failed }, default));

        var steps = await store.GetStepRunsAsync(new WorkflowRunId("r1"), default);
        Assert.Equal(["s1", "s2"], steps.Select(s => s.Id.Value));
        Assert.Equal(StepRunStatus.Running, steps[0].Status);
        Assert.Equal(s1b.Revision, steps[0].Revision);
        Assert.Empty(await store.GetStepRunsAsync(new WorkflowRunId("other"), default));
    }

    [Fact]
    public async Task Idempotency_key_is_unique_per_workflow_and_findable()
    {
        var store = CreateStore();
        var first = await store.SaveAsync(Run("r1", "k"), default);
        var ex = await Assert.ThrowsAsync<DuplicateIdempotencyKeyException>(async () => await store.SaveAsync(Run("r2", "k"), default));
        Assert.Equal(first.Id, ex.ExistingRun);
        Assert.Null(await store.GetRunAsync(new WorkflowRunId("r2"), default));

        await store.SaveAsync(Run("r3", "k", workflow: "other"), default); // same key, different workflow: allowed
        await store.SaveAsync(Run("r4"), default); // no key: never conflicts
        await store.SaveAsync(Run("r5"), default);

        Assert.Equal("r1", (await store.FindRunByIdempotencyKeyAsync(new WorkflowId("wf"), "k", default))!.Id.Value);
        Assert.Equal("r3", (await store.FindRunByIdempotencyKeyAsync(new WorkflowId("other"), "k", default))!.Id.Value);
        Assert.Null(await store.FindRunByIdempotencyKeyAsync(new WorkflowId("wf"), "nope", default));
    }

    [Fact]
    public async Task ListRuns_filters_by_status_oldest_first()
    {
        var store = CreateStore();
        var a = await store.SaveAsync(Run("a"), default);
        var b = await store.SaveAsync(Run("b"), default);
        await store.SaveAsync(Run("c"), default);
        await store.SaveAsync(b with { Status = WorkflowRunStatus.Running }, default);
        await store.SaveAsync(a with { Status = WorkflowRunStatus.Running }, default);

        var running = await store.ListRunsAsync([WorkflowRunStatus.Running], default);
        Assert.Equal(["a", "b"], running.Select(r => r.Id.Value));
        var both = await store.ListRunsAsync([WorkflowRunStatus.Running, WorkflowRunStatus.Pending], default);
        Assert.Equal(["a", "b", "c"], both.Select(r => r.Id.Value));
        Assert.Empty(await store.ListRunsAsync([], default));
        Assert.Empty(await store.ListRunsAsync([WorkflowRunStatus.Failed], default));
    }

    [Fact]
    public async Task Checkpoints_get_per_run_sequences_and_come_back_in_order()
    {
        var store = CreateStore();
        RunCheckpoint Cp(string run, CheckpointKind kind) => new(new WorkflowRunId(run), 0, kind, T0, new StepId("a"), new StepRunId("s"), "ok", new StepId("b"), 3);

        Assert.Equal(1, (await store.AppendCheckpointAsync(Cp("r1", CheckpointKind.StepCompleted), default)).Sequence);
        Assert.Equal(2, (await store.AppendCheckpointAsync(Cp("r1", CheckpointKind.Waiting), default)).Sequence);
        Assert.Equal(1, (await store.AppendCheckpointAsync(Cp("r2", CheckpointKind.Recovered), default)).Sequence);

        var list = await store.GetCheckpointsAsync(new WorkflowRunId("r1"), default);
        Assert.Equal([CheckpointKind.StepCompleted, CheckpointKind.Waiting], list.Select(c => c.Kind));
        Assert.Equal([1L, 2L], list.Select(c => c.Sequence));
        Assert.Equal("ok", list[0].Outcome);
        Assert.Equal(3, list[0].RunRevision);
    }

    [Fact]
    public async Task Concurrent_saves_from_the_same_revision_let_exactly_one_win()
    {
        var store = CreateStore();
        var v1 = await store.SaveAsync(Run("r1"), default);
        var attempts = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try { await store.SaveAsync(v1 with { CorrelationId = $"writer-{i}" }, default); return true; }
            catch (RunConcurrencyException) { return false; }
        })).ToArray();

        var results = await Task.WhenAll(attempts);
        Assert.Single(results, won => won);
        Assert.Equal(2, (await store.GetRunAsync(v1.Id, default))!.Revision);
    }

    [Fact]
    public async Task Concurrent_inserts_with_one_idempotency_key_create_one_run()
    {
        var store = CreateStore();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try { await store.SaveAsync(Run($"r{i}", "same"), default); return true; }
            catch (DuplicateIdempotencyKeyException) { return false; }
        })));
        Assert.Single(results, created => created);
        Assert.Single(await store.ListRunsAsync([WorkflowRunStatus.Pending], default));
    }
}

public sealed class InMemoryRunStoreContractTests : RunStoreContractTests
{
    protected override IRunStore CreateStore() => new InMemoryRunStore();
}

public sealed class SqliteRunStoreContractTests : RunStoreContractTests
{
    private readonly TempDb _db = new();
    protected override IRunStore CreateStore() => new SqliteRunStore(_db.Path);
    public override void Dispose() => _db.Dispose();
}

/// <summary>A database file in a private temp directory, removed on dispose.</summary>
public sealed class TempDb : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("agentic-forge-").FullName;
    public string Path => System.IO.Path.Combine(_dir, "runs.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
