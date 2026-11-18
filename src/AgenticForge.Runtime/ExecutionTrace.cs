using AgenticForge.Domain;

namespace AgenticForge.Runtime;

/// <summary>Renders a stored run as a stable, human-readable trace (no timestamps, so it is diffable across providers).</summary>
public static class ExecutionTrace
{
    public static IReadOnlyList<string> Render(WorkflowRunSnapshot run, IReadOnlyList<StepRunSnapshot> steps)
    {
        var lines = new List<string> { $"run {run.WorkflowId}@{run.WorkflowVersion}" };
        foreach (var s in steps)
        {
            var detail = s.Status switch
            {
                StepRunStatus.Succeeded => $" outcome={s.Outcome}",
                StepRunStatus.Failed => $" failure={s.Failure?.Kind}/{s.Failure?.Code}",
                StepRunStatus.Cancelled => $" reason={s.CancellationReason}",
                _ => "",
            };
            lines.Add($"  step {s.StepId} attempt={s.Attempt} {s.Status}{detail}");
        }
        var end = run.Status switch
        {
            WorkflowRunStatus.Failed => $" failure={run.Failure?.Kind}/{run.Failure?.Code}",
            WorkflowRunStatus.Cancelled => $" reason={run.CancellationReason}",
            _ => "",
        };
        lines.Add($"  => {run.Status}{end}");
        return lines;
    }

    /// <summary>
    /// Reconstructs the same trace as <see cref="Render"/> from recorded events alone (no run or step-run snapshots).
    /// Works from the audit channel, so it still holds when telemetry has been dropped.
    /// </summary>
    public static IReadOnlyList<string> RenderFromEvents(IReadOnlyList<ExecutionEvent> events)
    {
        var ordered = events.OrderBy(e => e.Sequence).ToList();
        var first = ordered.FirstOrDefault(e => e.Kind == ExecutionEventKind.RunStarted) ?? ordered.FirstOrDefault();
        if (first is null) return [];
        var lines = new List<string> { $"run {first.WorkflowId}@{first.WorkflowVersion}" };

        var stepLine = new Dictionary<StepRunId, int>();
        foreach (var e in ordered.Where(e => e.StepRunId is not null))
        {
            var status = e.Kind switch
            {
                ExecutionEventKind.StepStarted => "Running",
                ExecutionEventKind.StepAwaitingInput => "AwaitingInput",
                ExecutionEventKind.StepAwaitingApproval => "AwaitingApproval",
                ExecutionEventKind.StepSucceeded => "Succeeded",
                ExecutionEventKind.StepFailed => "Failed",
                ExecutionEventKind.StepCancelled => "Cancelled",
                _ => null,
            };
            if (status is null) continue;
            var detail = e.Kind switch
            {
                ExecutionEventKind.StepSucceeded => $" outcome={e.Outcome}",
                ExecutionEventKind.StepFailed => $" failure={e.Failure?.Kind}/{e.Failure?.Code}",
                ExecutionEventKind.StepCancelled => $" reason={Reason(e)}",
                _ => "",
            };
            var line = $"  step {e.StepId} attempt={e.Attempt} {status}{detail}";
            if (stepLine.TryGetValue(e.StepRunId!.Value, out var at)) lines[at] = line;
            else { stepLine[e.StepRunId.Value] = lines.Count; lines.Add(line); }
        }

        var end = ordered.LastOrDefault(e => e.Kind is ExecutionEventKind.RunSucceeded or ExecutionEventKind.RunFailed
            or ExecutionEventKind.RunCancelled or ExecutionEventKind.RunPaused);
        lines.Add(end?.Kind switch
        {
            ExecutionEventKind.RunSucceeded => "  => Succeeded",
            ExecutionEventKind.RunFailed => $"  => Failed failure={end.Failure?.Kind}/{end.Failure?.Code}",
            ExecutionEventKind.RunCancelled => $"  => Cancelled reason={Reason(end)}",
            _ => "  => Paused",
        });
        return lines;
    }

    private static string? Reason(ExecutionEvent e) => e.Attributes is { } a && a.TryGetValue("reason", out var r) ? r : null;
}
