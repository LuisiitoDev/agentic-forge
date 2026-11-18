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
}
