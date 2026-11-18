using AgenticForge.Application;
using AgenticForge.Domain;

namespace AgenticForge.Runtime;

/// <summary>
/// Non-durable store for tests and local runs. Implements the same revision, idempotency and checkpoint rules as durable
/// stores so engine behavior is identical. Step runs are returned in order of first insertion.
/// </summary>
public sealed class InMemoryRunStore : IRunStore
{
    private readonly object _gate = new();
    private readonly Dictionary<WorkflowRunId, WorkflowRunSnapshot> _runs = [];
    private readonly Dictionary<WorkflowRunId, List<StepRunSnapshot>> _steps = [];
    private readonly Dictionary<WorkflowRunId, List<RunCheckpoint>> _checkpoints = [];
    private readonly Dictionary<(WorkflowId, string), WorkflowRunId> _keys = [];
    private readonly List<WorkflowRunId> _order = [];

    public ValueTask<WorkflowRunSnapshot> SaveAsync(WorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var exists = _runs.TryGetValue(run.Id, out var stored);
            if (run.Revision != (exists ? stored!.Revision : 0) || (exists && run.Revision == 0))
                throw new RunConcurrencyException("WorkflowRun", run.Id.Value, run.Revision, exists ? stored!.Revision : null);
            if (!exists && run.IdempotencyKey is { } key)
            {
                if (_keys.TryGetValue((run.WorkflowId, key), out var other))
                    throw new DuplicateIdempotencyKeyException(run.WorkflowId, key, other);
                _keys[(run.WorkflowId, key)] = run.Id;
            }
            if (!exists) _order.Add(run.Id);
            var saved = run with { Revision = run.Revision + 1 };
            _runs[run.Id] = saved;
            return ValueTask.FromResult(saved);
        }
    }

    public ValueTask<StepRunSnapshot> SaveAsync(StepRunSnapshot stepRun, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_steps.TryGetValue(stepRun.RunId, out var list)) _steps[stepRun.RunId] = list = [];
            var i = list.FindIndex(s => s.Id == stepRun.Id);
            if (stepRun.Revision != (i >= 0 ? list[i].Revision : 0) || (i >= 0 && stepRun.Revision == 0))
                throw new RunConcurrencyException("StepRun", stepRun.Id.Value, stepRun.Revision, i >= 0 ? list[i].Revision : null);
            var saved = stepRun with { Revision = stepRun.Revision + 1 };
            if (i >= 0) list[i] = saved; else list.Add(saved);
            return ValueTask.FromResult(saved);
        }
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

    public ValueTask<WorkflowRunSnapshot?> FindRunByIdempotencyKeyAsync(WorkflowId workflow, string key, CancellationToken cancellationToken)
    {
        lock (_gate)
            return ValueTask.FromResult(_keys.TryGetValue((workflow, key), out var id) ? _runs[id] : null);
    }

    public ValueTask<IReadOnlyList<WorkflowRunSnapshot>> ListRunsAsync(IReadOnlyCollection<WorkflowRunStatus> statuses, CancellationToken cancellationToken)
    {
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<WorkflowRunSnapshot>>(
                [.. _order.Select(id => _runs[id]).Where(r => statuses.Contains(r.Status))]);
    }

    public ValueTask<RunCheckpoint> AppendCheckpointAsync(RunCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_checkpoints.TryGetValue(checkpoint.RunId, out var list)) _checkpoints[checkpoint.RunId] = list = [];
            var saved = checkpoint with { Sequence = list.Count + 1 };
            list.Add(saved);
            return ValueTask.FromResult(saved);
        }
    }

    public ValueTask<IReadOnlyList<RunCheckpoint>> GetCheckpointsAsync(WorkflowRunId id, CancellationToken cancellationToken)
    {
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<RunCheckpoint>>(
                _checkpoints.TryGetValue(id, out var list) ? [.. list] : []);
    }
}
