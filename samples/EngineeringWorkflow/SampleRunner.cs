using AgenticForge.Application;
using AgenticForge.Domain;
using AgenticForge.Persistence.Sqlite;
using AgenticForge.Runtime;

namespace AgenticForge.Samples.EngineeringWorkflow;

/// <summary>
/// Narrated end-to-end scenario used by <c>dotnet run</c> and by the tests. Runs the whole workflow on fake adapters over a SQLite
/// file and simulates a process restart while the run waits for human approval.
/// </summary>
public static class SampleRunner
{
    public const string ExternalReference = "issue://example/repo/42";

    public static async Task<WorkflowRunSnapshot> RunAsync(TextWriter output, string databasePath, int reviewRounds = 1,
        ApprovalDecisionKind finalDecision = ApprovalDecisionKind.Approved, CancellationToken cancellationToken = default)
    {
        var options = new SampleOptions(reviewRounds);

        // Process 1: start the run; it stops by itself at the plan approval.
        WorkflowRunSnapshot run;
        await using (var first = await EngineeringHost.CreateAsync(new SqliteRunStore(databasePath), options, cancellationToken: cancellationToken))
        {
            run = await first.StartAsync("Add dark mode", "Users want a dark theme.", ExternalReference, "request-42", cancellationToken);
            var pending = (await first.PendingApprovalAsync(run.Id, cancellationToken))!;
            output.WriteLine($"[process 1] run {run.Id} is {run.Status} at step '{run.CurrentStep}'; waiting for approval of {pending.ApprovalRequest!.Binding.Digest[..19]}...");
        }

        // Process 2: a fresh host, as after a restart. Everything it knows comes from the store.
        await using var host = await EngineeringHost.CreateAsync(new SqliteRunStore(databasePath), options, cancellationToken: cancellationToken);
        output.WriteLine("[process 2] restarted; state loaded from SQLite");

        try
        {
            await host.DecideAsync(run.Id, "build-bot", cancellationToken: cancellationToken);
        }
        catch (ApprovalDeniedException denied)
        {
            output.WriteLine($"[process 2] approval by 'build-bot' denied: {denied.Decision.Code}");
        }

        // The plan is approved. If the single rework does not satisfy the reviewer, the run pauses again at 'escalate'
        // and the human decision there is <paramref name="finalDecision"/>.
        var decision = ApprovalDecisionKind.Approved;
        while (true)
        {
            run = await host.DecideAsync(run.Id, EngineeringHost.Approver, decision, "reviewed", cancellationToken: cancellationToken);
            output.WriteLine($"[process 2] '{EngineeringHost.Approver}' decided {decision}; run is {run.Status}" + (run.Status == WorkflowRunStatus.Paused ? $" at '{run.CurrentStep}'" : ""));
            if (run.Status != WorkflowRunStatus.Paused) break;
            decision = finalDecision;
        }

        output.WriteLine();
        output.WriteLine("Execution trace:");
        foreach (var line in await host.TraceAsync(run.Id, cancellationToken)) output.WriteLine(line);

        output.WriteLine();
        output.WriteLine("Artifacts (step -> id, kind, digest):");
        foreach (var step in await host.Store.GetStepRunsAsync(run.Id, cancellationToken))
            foreach (var a in step.Artifacts ?? [])
                output.WriteLine($"  {step.StepId} -> {a.Id} ({a.Kind}) {a.Digest![..19]}...");

        output.WriteLine();
        output.WriteLine("Audit events recorded by this process:");
        foreach (var e in await host.Events.GetEventsAsync(run.Id, EventChannel.Audit, cancellationToken))
            output.WriteLine($"  #{e.Sequence} {e.Kind}" + (e.StepId is { } id ? $" step={id}" : "") + (e.Outcome is { } o ? $" outcome={o}" : "") + (e.Actor is { } a ? $" actor={a}" : ""));
        return run;
    }
}
