using System.Text.Json;
using AgenticForge.Abstractions;
using AgenticForge.Application;
using AgenticForge.Domain;
using AgenticForge.Runtime;

namespace AgenticForge.Runtime.Tests;

/// <summary>Deterministic agent driven by a script. No LLM, network or platform account.</summary>
internal sealed class FakeAgent(string id, Func<AgentInvocationRequest, CancellationToken, ValueTask<AgentInvocationResult>> script,
    params string[] capabilities) : IAgent
{
    public AgentDescriptor Descriptor { get; } = new(new AgentId(id), id, capabilities);
    public List<AgentInvocationRequest> Calls { get; } = [];

    public static FakeAgent Returning(string id, params AgentInvocationResult[] results)
    {
        var i = 0;
        return new FakeAgent(id, (_, _) => ValueTask.FromResult(results[Math.Min(i++, results.Length - 1)]));
    }

    public ValueTask<AgentInvocationResult> InvokeAsync(AgentInvocationRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        return script(request, ct);
    }
}

internal sealed class FakeTools(Func<ToolInvocationRequest, ToolInvocationResult>? script = null) : IToolProvider, IToolResolver
{
    public IReadOnlyList<ToolDescriptor> Tools { get; } = [new(new ToolName("echo"), "echo", ToolSideEffects.None)];
    public int Calls;
    public ValueTask<ToolInvocationResult> InvokeAsync(ToolInvocationRequest r, CancellationToken ct)
    {
        Calls++;
        return ValueTask.FromResult(script?.Invoke(r) ?? ToolInvocationResult.Success(r.Arguments));
    }
    public IToolProvider? Resolve(ToolName name) => name.Value == "echo" ? this : null;
}

internal sealed class FakeAgents : IAgentResolver
{
    private readonly Dictionary<string, IAgent> _agents = [];
    public FakeAgents Add(string name, IAgent agent) { _agents[name] = agent; return this; }
    public IAgent? Resolve(AgentId id) => _agents.GetValueOrDefault(id.Value);
}

internal sealed class FakeCatalog(params WorkflowDefinition[] defs) : IWorkflowCatalog
{
    public ValueTask<WorkflowDefinition?> FindAsync(WorkflowId id, WorkflowVersion? version, CancellationToken ct)
        => ValueTask.FromResult(defs.FirstOrDefault(d => d.Id == id && (version is null || d.Version == version)));
}

internal sealed class SequentialIds : IIdGenerator
{
    private int _n;
    public string NewId(string prefix) => $"{prefix}-{++_n}";
}

/// <summary>Manual clock: timers and delays fire only when the test advances time.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> _timers = [];

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(t);
        t.Change(dueTime, period);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.Due is { } d && d <= _now).ToList();
            foreach (var t in due) t.Due = null;
        }
        foreach (var t in due) t.Fire();
    }

    public int PendingTimers { get { lock (_gate) return _timers.Count(t => t.Due is not null); } }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            if (dueTime == TimeSpan.Zero) { lock (owner._gate) Due = null; Fire(); }
            return true;
        }
        public void Dispose() { lock (owner._gate) Due = null; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal static class W
{
    public static StepId S(string id) => new(id);
    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static WorkflowDefinition Def(StepDefinition[] steps, params Transition[] transitions)
        => new(new WorkflowId("wf"), new WorkflowVersion(1, 0, 0), "test", steps[0].Id, steps, transitions);

    public static StepDefinition Agent(string id, string target, string[]? outcomes = null, RetryPolicy? retry = null,
        TimeSpan? timeout = null, string[]? caps = null)
        => new(S(id), StepKind.Agent, target, caps, outcomes, retry, timeout);

    public static Transition T(string from, string outcome, string? to = null)
        => new(S(from), outcome, to is null ? null : S(to));

    /// <summary>a --ok--> b --done--> end.</summary>
    public static WorkflowDefinition TwoStep() => Def(
        [Agent("a", "agent-a", ["ok"]), Agent("b", "agent-b", ["done"])],
        T("a", "ok", "b"), T("b", "done"));

    public static Harness Build(WorkflowDefinition def, FakeAgents agents, FakeTools? tools = null, ManualTime? time = null,
        RuntimeOptions? options = null)
    {
        var store = new InMemoryRunStore();
        var events = new InMemoryEventLog();
        var orch = new WorkflowOrchestrator(new FakeCatalog(def), store, agents, tools ?? new FakeTools(), new SequentialIds(),
            time ?? new ManualTime(), options, events);
        return new Harness(orch, store, events);
    }
}

internal sealed record Harness(WorkflowOrchestrator Orchestrator, InMemoryRunStore Store, InMemoryEventLog Events)
{
    public ValueTask<WorkflowRunSnapshot> Start(CancellationToken ct = default, JsonElement? input = null)
        => Orchestrator.StartAsync(new StartRunCommand(new WorkflowId("wf"), input), ct);

    public async Task<IReadOnlyList<string>> Trace(WorkflowRunSnapshot run)
        => ExecutionTrace.Render(run, await Store.GetStepRunsAsync(run.Id, default));
}
