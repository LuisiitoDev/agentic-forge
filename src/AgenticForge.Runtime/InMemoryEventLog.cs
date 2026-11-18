using AgenticForge.Application;
using AgenticForge.Domain;

namespace AgenticForge.Runtime;

/// <summary>Non-durable event sink and history reader for tests and local runs. Assigns per-run sequence numbers on append.</summary>
public sealed class InMemoryEventLog : IExecutionEventSink, IExecutionHistory
{
    private readonly object _gate = new();
    private readonly Dictionary<WorkflowRunId, List<ExecutionEvent>> _events = [];

    public ValueTask AppendAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue(executionEvent.RunId, out var list)) _events[executionEvent.RunId] = list = [];
            list.Add(executionEvent with { Sequence = list.Count + 1 });
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<ExecutionEvent>> GetEventsAsync(WorkflowRunId run, EventChannel? channel, CancellationToken cancellationToken)
    {
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<ExecutionEvent>>(
                _events.TryGetValue(run, out var list) ? [.. list.Where(e => channel is null || e.Channel == channel)] : []);
    }

    public async ValueTask<IReadOnlyList<ArtifactRef>> GetArtifactsAsync(WorkflowRunId run, CancellationToken cancellationToken)
    {
        var seen = new HashSet<ArtifactId>();
        var result = new List<ArtifactRef>();
        foreach (var e in await GetEventsAsync(run, null, cancellationToken))
            foreach (var a in e.Artifacts ?? [])
                if (seen.Add(a.Id)) result.Add(a);
        return result;
    }
}

/// <summary>Sink that drops everything; the engine default when no sink is configured.</summary>
public sealed class NullEventSink : IExecutionEventSink
{
    public static NullEventSink Instance { get; } = new();
    public ValueTask AppendAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
