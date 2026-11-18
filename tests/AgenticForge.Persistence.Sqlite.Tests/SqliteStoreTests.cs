using AgenticForge.Application;
using AgenticForge.Domain;
using AgenticForge.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace AgenticForge.Persistence.Sqlite.Tests;

public class SqliteStoreTests
{
    [Fact]
    public async Task State_survives_closing_and_reopening_the_database()
    {
        using var db = new TempDb();
        var run = new WorkflowRunSnapshot(new WorkflowRunId("r1"), new WorkflowId("wf"), new WorkflowVersion(1, 0, 0), "c",
            WorkflowRunStatus.Running, DateTimeOffset.UnixEpoch, CurrentStep: new StepId("a"), IdempotencyKey: "k", RecoveryCount: 2);
        var step = new StepRunSnapshot(new StepRunId("s1"), run.Id, new StepId("a"), 1, StepRunStatus.AwaitingApproval,
            Approval: new ApprovalDecision("me", ApprovalDecisionKind.Approved, DateTimeOffset.UnixEpoch, "ok"));
        {
            var first = new SqliteRunStore(db.Path);
            await first.SaveAsync(run, default);
            await first.SaveAsync(step, default);
        }
        SqliteConnection.ClearAllPools(); // as if the process had exited

        var reopened = new SqliteRunStore(db.Path);
        Assert.Equal(run with { Revision = 1 }, await reopened.GetRunAsync(run.Id, default));
        Assert.Equal(step with { Revision = 1 }, Assert.Single(await reopened.GetStepRunsAsync(run.Id, default)));
        Assert.Equal(run.Id, (await reopened.FindRunByIdempotencyKeyAsync(run.WorkflowId, "k", default))!.Id);
    }

    [Fact]
    public async Task Two_store_instances_on_one_file_enforce_revisions_against_each_other()
    {
        using var db = new TempDb();
        var workerA = new SqliteRunStore(db.Path);
        var workerB = new SqliteRunStore(db.Path);
        var run = new WorkflowRunSnapshot(new WorkflowRunId("r1"), new WorkflowId("wf"), new WorkflowVersion(1, 0, 0), "c",
            WorkflowRunStatus.Running, DateTimeOffset.UnixEpoch);
        var saved = await workerA.SaveAsync(run, default);
        await workerA.SaveAsync(saved with { Status = WorkflowRunStatus.Paused }, default);

        await Assert.ThrowsAsync<RunConcurrencyException>(async () =>
            await workerB.SaveAsync(saved with { Status = WorkflowRunStatus.Cancelled }, default));
        Assert.Equal(WorkflowRunStatus.Paused, (await workerB.GetRunAsync(run.Id, default))!.Status);
    }

    [Fact]
    public void A_database_written_by_a_newer_store_schema_is_rejected()
    {
        using var db = new TempDb();
        _ = new SqliteRunStore(db.Path);
        using (var c = new SqliteConnection($"Data Source={db.Path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE meta SET value = '99' WHERE key = 'schemaVersion'";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        Assert.Throws<InvalidOperationException>(() => new SqliteRunStore(db.Path));
    }
}
