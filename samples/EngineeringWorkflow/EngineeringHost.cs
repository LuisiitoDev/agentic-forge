using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticForge.Application;
using AgenticForge.Domain;
using AgenticForge.Runtime;

namespace AgenticForge.Samples.EngineeringWorkflow;

/// <summary>Knobs of the sample scenario. Everything else is fixed by the workflow definition.</summary>
/// <param name="ReviewRoundsRequestingChanges">
/// How many consecutive reviews ask for changes: 0 passes the first review, 1 triggers one rework that then passes,
/// 2 or more exhausts the single allowed rework and escalates to a human.
/// </param>
/// <param name="Definition">Overrides the shipped definition (tests use this to show the runtime budget as a second guard).</param>
/// <param name="Runtime">Engine options, for example <see cref="RuntimeOptions.MaxStepRuns"/>.</param>
public sealed record SampleOptions(int ReviewRoundsRequestingChanges = 1, WorkflowDefinition? Definition = null, RuntimeOptions? Runtime = null);

/// <summary>Single-definition catalog. A real host would load definitions from files or a registry.</summary>
public sealed class SingleWorkflowCatalog(WorkflowDefinition definition) : IWorkflowCatalog
{
    public ValueTask<WorkflowDefinition?> FindAsync(WorkflowId id, WorkflowVersion? version, CancellationToken cancellationToken)
        => ValueTask.FromResult<WorkflowDefinition?>(definition.Id == id && (version is null || version == definition.Version) ? definition : null);
}

/// <summary>
/// The composition root of the sample: wires the fake adapters, governance and the engine through public contracts only.
/// Callers choose the <see cref="IRunStore"/>: in-memory for a quick run, SQLite to survive a restart.
/// </summary>
public sealed class EngineeringHost : IAsyncDisposable
{
    /// <summary>The only identity governance lets decide approvals in this sample.</summary>
    public const string Approver = "tech-lead";
    public static readonly WorkflowId WorkflowId = new("engineering-workflow");

    private readonly AdapterRegistry _registry;

    private EngineeringHost(AdapterRegistry registry, IRunStore store, InMemoryEventLog events, WorkflowOrchestrator orchestrator,
        WorkflowDefinition definition, FakePlanner planner, FakeImplementer implementer, FakeReviewer reviewer, FakePublisher publisher)
    {
        _registry = registry;
        Store = store;
        Events = events;
        Orchestrator = orchestrator;
        Definition = definition;
        Planner = planner;
        Implementer = implementer;
        Reviewer = reviewer;
        Publisher = publisher;
    }

    public IRunStore Store { get; }
    public InMemoryEventLog Events { get; }
    public WorkflowOrchestrator Orchestrator { get; }
    public WorkflowDefinition Definition { get; }
    public FakePlanner Planner { get; }
    public FakeImplementer Implementer { get; }
    public FakeReviewer Reviewer { get; }
    public FakePublisher Publisher { get; }

    /// <summary>Loads the shipped definition through the public loader. Throws if it is invalid (it is covered by tests).</summary>
    public static WorkflowDefinition LoadDefinition()
    {
        using var stream = typeof(EngineeringHost).Assembly.GetManifestResourceStream("engineering-workflow.json")
                           ?? throw new InvalidOperationException("The workflow definition resource is missing.");
        using var reader = new StreamReader(stream);
        var result = WorkflowLoader.Load(reader.ReadToEnd());
        return result.IsValid ? result.Definition! : throw new InvalidOperationException("Invalid workflow: " + string.Join("; ", result.Errors));
    }

    public static async ValueTask<EngineeringHost> CreateAsync(IRunStore store, SampleOptions? options = null,
        TimeProvider? time = null, CancellationToken cancellationToken = default)
    {
        options ??= new SampleOptions();
        var definition = options.Definition ?? LoadDefinition();
        var planner = new FakePlanner();
        var implementer = new FakeImplementer();
        var reviewer = new FakeReviewer(options.ReviewRoundsRequestingChanges);
        var publisher = new FakePublisher();

        var registry = new AdapterRegistry().AddAgent(planner).AddAgent(implementer).AddAgent(reviewer).AddToolProvider(publisher);
        await registry.InitializeAsync(cancellationToken);

        // Governance: capabilities are deny-by-default, the host sets a ceiling, and only the named human may decide approvals.
        var governance = new GovernanceOptions(
            AllowedCapabilities: new HashSet<string> { "read-requirements", "read-repo", "write-repo", FakePublisher.PublishTool },
            Approvers: new HashSet<string> { Approver });

        var events = new InMemoryEventLog();
        var orchestrator = new WorkflowOrchestrator(new SingleWorkflowCatalog(definition), store, registry, registry,
            time: time, options: options.Runtime, events: events, governance: governance);
        return new EngineeringHost(registry, store, events, orchestrator, definition, planner, implementer, reviewer, publisher);
    }

    /// <summary>
    /// Starts a run for a request. <paramref name="externalReference"/> is an opaque pointer to where the request came from
    /// (an issue URL, a ticket id); the idempotency key makes a redelivered request return the same run.
    /// </summary>
    public ValueTask<WorkflowRunSnapshot> StartAsync(string title, string body, string? externalReference = null,
        string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.SerializeToElement(new JsonObject { ["title"] = title, ["body"] = body });
        return Orchestrator.StartAsync(new StartRunCommand(WorkflowId, input, null, idempotencyKey, externalReference), cancellationToken);
    }

    /// <summary>The approval step run a paused run is waiting on, or null.</summary>
    public async ValueTask<StepRunSnapshot?> PendingApprovalAsync(WorkflowRunId run, CancellationToken cancellationToken = default)
        => (await Store.GetStepRunsAsync(run, cancellationToken)).LastOrDefault(s => s.Status == StepRunStatus.AwaitingApproval);

    /// <summary>
    /// Records a human decision the way an approval UI would: on exactly the binding digest the reviewer was shown.
    /// <paramref name="digest"/> defaults to the pending request's digest; pass another value to simulate a stale or forged approval.
    /// </summary>
    public async ValueTask<WorkflowRunSnapshot> DecideAsync(WorkflowRunId run, string approver, ApprovalDecisionKind kind = ApprovalDecisionKind.Approved,
        string? reason = null, string? digest = null, CancellationToken cancellationToken = default)
    {
        var pending = await PendingApprovalAsync(run, cancellationToken)
                      ?? throw new InvalidOperationException($"Run '{run}' is not waiting for an approval.");
        var decision = new ApprovalDecision(approver, kind, DateTimeOffset.UtcNow, reason, digest ?? pending.ApprovalRequest!.Binding.Digest);
        return await Orchestrator.ResumeAsync(ResumeRunCommand.WithDecision(run, decision), cancellationToken);
    }

    /// <summary>The stable, timestamp-free trace of a run, built from stored state.</summary>
    public async ValueTask<IReadOnlyList<string>> TraceAsync(WorkflowRunId run, CancellationToken cancellationToken = default)
        => ExecutionTrace.Render((await Store.GetRunAsync(run, cancellationToken))!, await Store.GetStepRunsAsync(run, cancellationToken));

    public ValueTask DisposeAsync() => _registry.DisposeAsync();
}
