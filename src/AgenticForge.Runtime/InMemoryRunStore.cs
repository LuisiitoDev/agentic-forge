using AgenticForge.Application;
using AgenticForge.Domain;

namespace AgenticForge.Runtime;

/// <summary>Non-durable store for tests and local runs. Step runs are returned in save order of first insertion.</summary>
public sealed class InMemoryRunStore : IRunStore
{
    private readonly object _gate = new();
    private readonly Dictionary<WorkflowRunId, WorkflowRunSnapshot> _runs = [];
    private readonly Dictionary<WorkflowRunId, List<StepRunSnapshot>> _steps = [];

    public ValueTask SaveAsync(WorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        lock (_gate) _runs[run.Id] = run;
        return ValueTask.CompletedTask;
    }

    public ValueTask SaveAsync(StepRunSnapshot stepRun, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_steps.TryGetValue(stepRun.RunId, out var list)) _steps[stepRun.RunId] = list = [];
            var i = list.FindIndex(s => s.Id == stepRun.Id);
            if (i >= 0) list[i] = stepRun; else list.Add(stepRun);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<WorkflowRunSnapshot?> GetRunAsync(WorkflowRunId id, CancellationToken cancellationToken)
    {
        lock (_gate) return ValueTask.FromResult(_runs.GetValueOrDefault(id));
    }

    public ValueTask<IReadOnlyList<StepRunSnapshot>> GetStepRunsAsync(WorkflowRunId id, CancellationToken cancellationToken)
    {
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<StepRunSnapshot>>(
                _steps.TryGetValue(id, out var list) ? [.. list] : []);
    }
}
