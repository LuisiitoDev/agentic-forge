using System.Text.Json;
using AgenticForge.Abstractions;
using AgenticForge.Adapters.Conformance;
using AgenticForge.Adapters.Conformance.Fakes;
using AgenticForge.Domain;

namespace AgenticForge.Adapters.Tests;

// Each class below is all a provider needs to write to prove its adapter satisfies the port contract.

public sealed class FakeAgentConformance : AgentConformance
{
    protected override IAgent CreateAdapter() => new FakeAgent();
    protected override IAgent? CreateFailingAdapter() => new FakeAgent(mode: FakeMode.RateLimited);
    protected override IAgent? CreateBlockingAdapter() => new FakeAgent(mode: FakeMode.Block);
}

/// <summary>A second, structurally different agent implementation passes the same suite.</summary>
public sealed class ModelBackedAgentConformance : AgentConformance
{
    protected override IAgent CreateAdapter() => new ModelBackedAgent("model-agent", new FakeModelInference());
    protected override IAgent? CreateFailingAdapter() => new ModelBackedAgent("model-agent", new FakeModelInference(mode: FakeMode.RateLimited));
    protected override IAgent? CreateBlockingAdapter() => new ModelBackedAgent("model-agent", new FakeModelInference(mode: FakeMode.Block));
}

public sealed class FakeModelConformance : ModelInferenceConformance
{
    protected override IModelInference CreateAdapter() => new FakeModelInference();
    protected override IModelInference? CreateFailingAdapter() => new FakeModelInference(mode: FakeMode.RateLimited);
    protected override IModelInference? CreateBlockingAdapter() => new FakeModelInference(mode: FakeMode.Block);
}

public sealed class FakeToolProviderConformance : ToolProviderConformance
{
    protected override IToolProvider CreateAdapter() => new FakeToolProvider();
    protected override IToolProvider? CreateFailingAdapter() => new FakeToolProvider(FakeMode.RateLimited);
    protected override IToolProvider? CreateBlockingAdapter() => new FakeToolProvider(FakeMode.Block);
    protected override ToolInvocationRequest CreateRequest(IToolProvider adapter)
        => new(adapter.Tools[0].Name, Context(), JsonDocument.Parse("{\"x\":1}").RootElement.Clone());
}

public sealed class FakeExecutionEnvironmentConformance : ExecutionEnvironmentConformance
{
    protected override IExecutionEnvironment CreateAdapter() => new FakeExecutionEnvironment();
    protected override IExecutionEnvironment? CreateFailingAdapter() => new FakeExecutionEnvironment(mode: FakeMode.RateLimited);
    protected override IExecutionEnvironment? CreateBlockingAdapter() => new FakeExecutionEnvironment(mode: FakeMode.Block);
}

public sealed class FakePlatformConformance : PlatformIntegrationConformance
{
    protected override IPlatformIntegration CreateAdapter() => new FakePlatformIntegration();
    protected override IPlatformIntegration? CreateFailingAdapter() => new FakePlatformIntegration(mode: FakeMode.RateLimited);
    protected override IPlatformIntegration? CreateBlockingAdapter() => new FakePlatformIntegration(mode: FakeMode.Block);
}

/// <summary>Self-checks of the suite's own rules, so a weak rule cannot silently pass bad adapters.</summary>
public sealed class FailureRulesTests
{
    [Fact]
    public void Internal_failures_are_not_valid_adapter_failures()
        => Assert.ThrowsAny<Exception>(() => FailureRules.AssertNormalized(new Failure(FailureKind.Internal, "x", "y")));

    [Fact]
    public void Provider_failures_must_keep_diagnostics()
        => Assert.ThrowsAny<Exception>(() => FailureRules.AssertNormalized(new Failure(FailureKind.Provider, "x", "y")));
}
