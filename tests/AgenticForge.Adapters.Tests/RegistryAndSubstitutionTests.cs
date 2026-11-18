using System.Reflection;
using AgenticForge.Abstractions;
using AgenticForge.Adapters.Conformance.Fakes;
using AgenticForge.Application;
using AgenticForge.Domain;
using AgenticForge.Runtime;

namespace AgenticForge.Adapters.Tests;

public sealed class RegistryTests
{
    [Fact]
    public async Task Resolves_registered_adapters_and_reports_capabilities()
    {
        await using var registry = new AdapterRegistry()
            .AddAgent(new FakeAgent("writer")).AddToolProvider(new FakeToolProvider())
            .AddModel(new FakeModelInference()).AddExecutionEnvironment(new FakeExecutionEnvironment()).AddPlatform(new FakePlatformIntegration());
        Assert.NotNull(registry.Resolve(new AgentId("writer")));
        Assert.Null(registry.Resolve(new AgentId("other")));
        Assert.NotNull(registry.Resolve(new ToolName("echo")));
        Assert.NotNull(registry.GetModel("fake-model"));
        Assert.NotNull(registry.GetExecutionEnvironment("fake-exec"));
        Assert.NotNull(registry.GetPlatform("fake-platform"));
        var report = registry.Describe();
        Assert.Contains(report, d => d.Kind == AdapterKind.Platform && d.Capabilities.Contains("create-pull-request"));
        Assert.Contains(report, d => d.Kind == AdapterKind.Agent && d.Id == "writer");
        Assert.Contains(report, d => d.Kind == AdapterKind.Tool && d.Id == "echo");
    }

    [Fact]
    public void Duplicates_and_invalid_descriptors_are_rejected()
    {
        var registry = new AdapterRegistry().AddAgent(new FakeAgent("a")).AddModel(new FakeModelInference("m")).AddToolProvider(new FakeToolProvider());
        Assert.Throws<AdapterRegistrationException>(() => registry.AddAgent(new FakeAgent("a")));
        Assert.Throws<AdapterRegistrationException>(() => registry.AddModel(new FakeModelInference("m")));
        Assert.Throws<AdapterRegistrationException>(() => registry.AddToolProvider(new FakeToolProvider()));
        Assert.Throws<AdapterRegistrationException>(() => registry.AddModel(new FakeModelInference(" ")));
    }

    [Fact]
    public async Task Lifecycle_initializes_in_order_disposes_in_reverse_and_blocks_late_registration()
    {
        var order = new List<string>();
        var registry = new AdapterRegistry().AddModel(new Tracked("one", order)).AddModel(new Tracked("two", order));
        await registry.InitializeAsync(default);
        Assert.Throws<AdapterRegistrationException>(() => registry.AddModel(new FakeModelInference("late")));
        await registry.DisposeAsync();
        await registry.DisposeAsync();
        Assert.Equal(["init one", "init two", "dispose two", "dispose one"], order);
    }

    [Fact]
    public async Task Failed_initialization_disposes_what_was_started_and_names_the_adapter()
    {
        var order = new List<string>();
        var registry = new AdapterRegistry().AddModel(new Tracked("one", order)).AddModel(new Tracked("two", order, failInit: true));
        var ex = await Assert.ThrowsAsync<AdapterInitializationException>(async () => await registry.InitializeAsync(default));
        Assert.Equal(nameof(Tracked), ex.Adapter);
        Assert.Equal(["init one", "dispose one"], order);
    }

    private sealed class Tracked(string id, List<string> log, bool failInit = false) : IModelInference, IAdapterLifecycle
    {
        public AdapterDescriptor Descriptor { get; } = new(id, AdapterKind.ModelInference, "1", []);
        public ValueTask<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct) => ValueTask.FromResult(ModelResult.Success("x"));
        public ValueTask InitializeAsync(CancellationToken ct)
        {
            if (failInit) throw new InvalidOperationException("no");
            log.Add("init " + id);
            return ValueTask.CompletedTask;
        }
        public ValueTask<AdapterHealth> CheckHealthAsync(CancellationToken ct) => ValueTask.FromResult(new AdapterHealth(true));
        public ValueTask DisposeAsync() { log.Add("dispose " + id); return ValueTask.CompletedTask; }
    }
}

/// <summary>Runs one workflow definition end to end on swapped adapters, with no external account.</summary>
public sealed class SubstitutionTests
{
    private sealed class Catalog(WorkflowDefinition def) : IWorkflowCatalog
    {
        public ValueTask<WorkflowDefinition?> FindAsync(WorkflowId id, WorkflowVersion? version, CancellationToken ct) => ValueTask.FromResult<WorkflowDefinition?>(def);
    }

    private static readonly WorkflowDefinition Def = new(
        new WorkflowId("wf"), new WorkflowVersion(1, 0, 0), "adapters", new StepId("write"),
        [new StepDefinition(new StepId("write"), StepKind.Agent, "writer", ["read-requirements"], ["done"], Timeout: TimeSpan.FromMilliseconds(300))],
        [new Transition(new StepId("write"), "done", null)]);

    private static WorkflowOrchestrator Engine(AdapterRegistry registry)
        => new(new Catalog(Def), new InMemoryRunStore(), registry, registry);

    private static StartRunCommand Start() => new(new WorkflowId("wf"));

    [Fact]
    public async Task A_second_agent_implementation_runs_the_same_workflow_unchanged()
    {
        await using var first = new AdapterRegistry().AddAgent(new FakeAgent("writer"));
        await using var second = new AdapterRegistry().AddAgent(new ModelBackedAgent("writer", new FakeModelInference()));

        var a = await Engine(first).StartAsync(Start(), default);
        var b = await Engine(second).StartAsync(Start(), default);

        Assert.Equal(WorkflowRunStatus.Succeeded, a.Status);
        Assert.Equal(WorkflowRunStatus.Succeeded, b.Status);
        Assert.Equal(a.Output?.GetRawText(), b.Output?.GetRawText());
    }

    [Fact]
    public async Task Swapping_the_model_adapter_changes_nothing_in_the_core()
    {
        var one = new FakeModelInference("model-one", reply: "A");
        var two = new FakeModelInference("model-two", reply: "B");
        foreach (var model in new[] { one, two })
        {
            await using var registry = new AdapterRegistry().AddModel(model).AddAgent(new ModelBackedAgent("writer", model));
            await registry.InitializeAsync(default);
            Assert.Equal(WorkflowRunStatus.Succeeded, (await Engine(registry).StartAsync(Start(), default)).Status);
            Assert.Single(model.Calls);
        }
    }

    [Fact]
    public async Task Provider_errors_surface_as_normalized_run_failures_with_diagnostics_and_no_secrets()
    {
        await using var registry = new AdapterRegistry().AddAgent(new ModelBackedAgent("writer", new FakeModelInference(mode: FakeMode.RateLimited)));
        var run = await Engine(registry).StartAsync(Start(), default);

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        var f = run.Failure!;
        Assert.Equal((FailureKind.Provider, "provider_rate_limited", true), (f.Kind, f.Code, f.Retryable));
        Assert.Equal("429", f.Details![ProviderFailures.ProviderCodeKey]);
        Assert.Equal("req-1", f.Details["requestId"]);
        Assert.Equal(ProviderFailures.Redacted, f.Details["apiKey"]);
    }

    [Fact]
    public async Task A_step_timeout_stops_a_hung_adapter_and_is_a_timeout_failure()
    {
        await using var registry = new AdapterRegistry().AddAgent(new FakeAgent("writer", FakeMode.Block));
        var run = await Engine(registry).StartAsync(Start(), default);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(FailureKind.Timeout, run.Failure!.Kind);
    }

    [Fact]
    public async Task Host_cancellation_stops_a_hung_adapter_and_is_cancellation_not_failure()
    {
        var def = Def with { Steps = [Def.Steps[0] with { Timeout = null }] };
        await using var registry = new AdapterRegistry().AddAgent(new FakeAgent("writer", FakeMode.Block));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var engine = new WorkflowOrchestrator(new Catalog(def), new InMemoryRunStore(), registry, registry);
        var run = await engine.StartAsync(Start(), cts.Token);
        Assert.Equal(WorkflowRunStatus.Cancelled, run.Status);
        Assert.Null(run.Failure);
    }

    [Fact]
    public void Core_assemblies_reference_no_provider_sdks()
    {
        var allowed = new[] { "System", "Microsoft", "netstandard", "mscorlib", "AgenticForge" };
        foreach (var assembly in new[] { typeof(WorkflowId), typeof(IAgent), typeof(IWorkflowOrchestrator), typeof(WorkflowOrchestrator) }.Select(t => t.Assembly))
        {
            var offenders = assembly.GetReferencedAssemblies().Select(a => a.Name!)
                .Where(n => !allowed.Any(p => n == p || n.StartsWith(p + ".", StringComparison.Ordinal))).ToList();
            Assert.True(offenders.Count == 0, $"{assembly.GetName().Name} references {string.Join(", ", offenders)}");
        }
    }
}
