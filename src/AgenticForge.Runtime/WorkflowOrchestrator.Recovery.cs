using AgenticForge.Application;
using AgenticForge.Domain;

namespace AgenticForge.Runtime;

// Recovery of runs interrupted by a restart or crash. Rules (see docs/design/persistence-and-recovery.md):
//  * the stored step runs are the source of truth; a step run that Succeeded is never executed again;
//  * an interrupted step run (Running) is failed with a retryable "interrupted_by_restart" and re-attempted only if the
//    step declares a retry policy with attempts left, otherwise the run fails (we cannot know what the step already did);
//  * the first save of the recovery claims the run; a competing worker gets a RunConcurrencyException and backs off.
public sealed partial class WorkflowOrchestrator
{
    public const string InterruptedFailureCode = "interrupted_by_restart";

    public async ValueTask<RecoveryResult> RecoverAsync(WorkflowRunId runId, CancellationToken cancellationToken)
    {
        var snapshot = await _store.GetRunAsync(runId, cancellationToken)
                       ?? throw new KeyNotFoundException($"Run '{runId}' was not found.");
        try
        {
            return await RecoverCoreAsync(snapshot, cancellationToken);
        }
        catch (RunConcurrencyException)
        {
            return new RecoveryResult(runId, RecoveryOutcome.ClaimedElsewhere);
        }
    }

    public async ValueTask<IReadOnlyList<RecoveryResult>> RecoverAllAsync(CancellationToken cancellationToken)
    {
        var candidates = await _store.ListRunsAsync(
            [WorkflowRunStatus.Pending, WorkflowRunStatus.Running, WorkflowRunStatus.Paused], cancellationToken);
        var results = new List<RecoveryResult>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await RecoverAsync(candidate.Id, cancellationToken);
                if (result.Outcome != RecoveryOutcome.NotInterrupted) results.Add(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // One broken run must not stop recovery of the others. Only the type is reported (messages may carry secrets).
                results.Add(new RecoveryResult(candidate.Id, RecoveryOutcome.Failed, Detail: e.GetType().Name));
            }
        }
        return results;
    }

    private async ValueTask<RecoveryResult> RecoverCoreAsync(WorkflowRunSnapshot snapshot, CancellationToken ct)
    {
        var id = snapshot.Id;
        if (snapshot.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled)
            return new RecoveryResult(id, RecoveryOutcome.NotInterrupted, snapshot);

        var steps = await _store.GetStepRunsAsync(id, ct);
        var last = steps.Count > 0 ? steps[^1] : null;

        // A paused run whose step is genuinely waiting is not interrupted: it resumes through ResumeAsync.
        if (snapshot.Status == WorkflowRunStatus.Paused
            && last is { Status: StepRunStatus.AwaitingInput or StepRunStatus.AwaitingApproval })
            return new RecoveryResult(id, RecoveryOutcome.NotInterrupted, snapshot);

        var def = await _catalog.FindAsync(snapshot.WorkflowId, snapshot.WorkflowVersion, ct);
        if (def is null)
            return new RecoveryResult(id, RecoveryOutcome.Failed, snapshot, $"Workflow '{snapshot.WorkflowId}' version '{snapshot.WorkflowVersion}' was not found.");
        var errors = def.Validate();
        if (errors.Count > 0)
            return new RecoveryResult(id, RecoveryOutcome.Failed, snapshot, "Workflow definition is invalid.");

        var run = WorkflowRun.Rehydrate(snapshot);
        var previous = snapshot.Status;

        if (previous == WorkflowRunStatus.Pending)
        {
            // Created but never started (crash between the two saves). Nothing has executed yet.
            run.Start(Now, def.Start);
            await SaveAsync(run);
            await CheckpointAsync(run, CheckpointKind.Recovered, def.Start, null, next: def.Start);
            await EmitAsync(run, ExecutionEventKind.RunStarted);
            await EmitAsync(run, ExecutionEventKind.RunRecovered, attributes: new Dictionary<string, string> { ["previousStatus"] = previous.ToString() });
            return Recovered(await DriveAsync(run, def, null, null, null, ct));
        }

        if (previous == WorkflowRunStatus.Paused) run.Resume(); // a half-applied resume: the step is no longer waiting
        run.MarkRecovered();
        await SaveAsync(run); // the claim: a competing worker saving from the old revision is rejected
        var current = run.State.CurrentStep!.Value;
        await CheckpointAsync(run, CheckpointKind.Recovered, current, last?.Id, next: current);
        await EmitAsync(run, ExecutionEventKind.RunRecovered, attributes: new Dictionary<string, string> { ["previousStatus"] = previous.ToString() });

        // The last step run belongs to the current step only if the interrupted work is on it. If the run already moved on,
        // the last step run belongs to an earlier step and the current step simply has not started.
        if (last is null || last.StepId != current)
            return Recovered(await DriveAsync(run, def, null, null, null, ct));

        var stepDef = StepOf(def, current);
        var step = StepRun.Rehydrate(last);
        switch (last.Status)
        {
            case StepRunStatus.Succeeded:
                // Finished before the crash: apply its recorded outcome instead of running it again.
                return Recovered(await DriveAsync(run, def,
                    StepResult.Done(last.Outcome!, last.Output, last.Artifacts, last.Id), null, null, ct));

            case StepRunStatus.AwaitingInput or StepRunStatus.AwaitingApproval:
                run.Pause();
                await SaveAsync(run);
                await CheckpointAsync(run, CheckpointKind.Waiting, current, last.Id);
                await EmitAsync(run, ExecutionEventKind.RunPaused);
                return Recovered(run.State);

            case StepRunStatus.Cancelled:
                return Recovered(await CancelRunAsync(run, null, last.CancellationReason));

            case StepRunStatus.Pending:
                // Never started, so it cannot have had side effects: start it now.
                step.Start(Now);
                await SaveAsync(step);
                await EmitAsync(run, ExecutionEventKind.StepStarted, step);
                return Recovered(await DriveAsync(run, def, null, step, null, ct));

            case StepRunStatus.Running:
                step.Fail(Now, new Failure(FailureKind.Internal, InterruptedFailureCode,
                    $"Step '{current}' was interrupted by a restart; its effects are unknown.", Retryable: true));
                await SaveAsync(step);
                await EmitStepEndAsync(run, step);
                return Recovered(await RetryOrFailAsync(run, def, stepDef, step.State, ct));

            default: // Failed before the run recorded it
                return Recovered(await RetryOrFailAsync(run, def, stepDef, last, ct));
        }
    }

    private async ValueTask<WorkflowRunSnapshot> RetryOrFailAsync(WorkflowRun run, WorkflowDefinition def, StepDefinition stepDef,
        StepRunSnapshot failed, CancellationToken ct)
    {
        var failure = failed.Failure ?? new Failure(FailureKind.Internal, "missing_failure", "A failed step run had no Failure.");
        var maxAttempts = stepDef.Retry?.MaxAttempts ?? 1;
        if (!failure.Retryable || failed.Attempt >= maxAttempts)
            return await FailRunAsync(run, failure);

        await EmitAsync(run, ExecutionEventKind.StepRetryScheduled, StepRun.Rehydrate(failed), failure: failure,
            attributes: new Dictionary<string, string> { ["nextAttempt"] = (failed.Attempt + 1).ToString() });
        return await DriveAsync(run, def, null, null, null, ct, failed.Attempt + 1);
    }

    private static RecoveryResult Recovered(WorkflowRunSnapshot snapshot) => new(snapshot.Id, RecoveryOutcome.Recovered, snapshot);
}
