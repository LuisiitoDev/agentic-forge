using System.Text.Json;
using AgenticForge.Abstractions;
using AgenticForge.Domain;

namespace AgenticForge.Adapters.Conformance;

public abstract class AgentConformance : PortConformance<IAgent, AgentInvocationRequest, AgentInvocationResult>
{
    protected override ValueTask<AgentInvocationResult> InvokeAsync(IAgent a, AgentInvocationRequest r, CancellationToken ct) => a.InvokeAsync(r, ct);
    protected override InvocationStatus StatusOf(AgentInvocationResult r) => r.Status;
    protected override Failure? FailureOf(AgentInvocationResult r) => r.Failure;

    protected override AgentInvocationRequest CreateRequest(IAgent adapter)
        => new(adapter.Descriptor.Id, Context(), JsonDocument.Parse("{\"task\":\"conformance\"}").RootElement.Clone());

    [Fact]
    public void Descriptor_is_complete()
    {
        var d = CreateAdapter().Descriptor;
        Assert.False(string.IsNullOrWhiteSpace(d.DisplayName));
        Assert.NotNull(d.Capabilities);
        Assert.DoesNotContain(d.Capabilities, string.IsNullOrWhiteSpace);
        Assert.Equal(d.Capabilities.Count, d.Capabilities.Distinct().Count());
    }

    [Fact]
    public async Task Success_names_an_outcome_for_the_workflow_to_route_on()
    {
        await using var scope = new AdapterScope(CreateAdapter());
        var result = await scope.Adapter.InvokeAsync(CreateRequest(scope.Adapter), CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(result.Outcome));
    }
}

public abstract class ToolProviderConformance : PortConformance<IToolProvider, ToolInvocationRequest, ToolInvocationResult>
{
    protected override ValueTask<ToolInvocationResult> InvokeAsync(IToolProvider a, ToolInvocationRequest r, CancellationToken ct) => a.InvokeAsync(r, ct);
    protected override InvocationStatus StatusOf(ToolInvocationResult r) => r.Status;
    protected override Failure? FailureOf(ToolInvocationResult r) => r.Failure;

    [Fact]
    public void Tool_names_are_unique_and_described()
    {
        var tools = CreateAdapter().Tools;
        Assert.NotEmpty(tools);
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct().Count());
        Assert.DoesNotContain(tools, t => string.IsNullOrWhiteSpace(t.Description));
    }

    [Fact]
    public async Task Unknown_tool_is_a_failure_not_an_exception()
    {
        await using var scope = new AdapterScope(CreateAdapter());
        var request = CreateRequest(scope.Adapter) with { Tool = new ToolName("no-such-tool-" + Guid.NewGuid().ToString("N")) };
        var result = await scope.Adapter.InvokeAsync(request, CancellationToken.None);
        Assert.Equal(InvocationStatus.Failed, result.Status);
        FailureRules.AssertNormalized(result.Failure!);
        Assert.False(result.Failure!.Retryable);
    }
}

public abstract class ModelInferenceConformance : PortConformance<IModelInference, ModelRequest, ModelResult>
{
    protected override ValueTask<ModelResult> InvokeAsync(IModelInference a, ModelRequest r, CancellationToken ct) => a.CompleteAsync(r, ct);
    protected override InvocationStatus StatusOf(ModelResult r) => r.Status;
    protected override Failure? FailureOf(ModelResult r) => r.Failure;

    protected override ModelRequest CreateRequest(IModelInference adapter)
        => new(Context(), "conformance-model", [new ModelMessage("user", "Say hello.")]);

    [Fact]
    public void Descriptor_is_a_valid_model_inference_descriptor()
    {
        var d = CreateAdapter().Descriptor;
        Assert.Empty(d.Validate());
        Assert.Equal(AdapterKind.ModelInference, d.Kind);
    }

    [Fact]
    public async Task Success_returns_text_and_non_negative_usage()
    {
        await using var scope = new AdapterScope(CreateAdapter());
        var result = await scope.Adapter.CompleteAsync(CreateRequest(scope.Adapter), CancellationToken.None);
        Assert.NotNull(result.Text);
        if (result.Usage is { } u) { Assert.True(u.InputTokens >= 0); Assert.True(u.OutputTokens >= 0); }
    }
}

public abstract class ExecutionEnvironmentConformance : PortConformance<IExecutionEnvironment, ExecutionRequest, ExecutionResult>
{
    protected override ValueTask<ExecutionResult> InvokeAsync(IExecutionEnvironment a, ExecutionRequest r, CancellationToken ct) => a.ExecuteAsync(r, ct);
    protected override InvocationStatus StatusOf(ExecutionResult r) => r.Status;
    protected override Failure? FailureOf(ExecutionResult r) => r.Failure;

    protected override ExecutionRequest CreateRequest(IExecutionEnvironment adapter) => new(Context(), "echo", ["hello"]);

    [Fact]
    public void Descriptor_is_a_valid_execution_environment_descriptor()
    {
        var d = CreateAdapter().Descriptor;
        Assert.Empty(d.Validate());
        Assert.Equal(AdapterKind.ExecutionEnvironment, d.Kind);
    }

    [Fact]
    public async Task Completed_execution_reports_an_exit_code()
    {
        await using var scope = new AdapterScope(CreateAdapter());
        var result = await scope.Adapter.ExecuteAsync(CreateRequest(scope.Adapter), CancellationToken.None);
        Assert.NotNull(result.ExitCode);
    }
}

public abstract class PlatformIntegrationConformance : PortConformance<IPlatformIntegration, PlatformRequest, PlatformResult>
{
    protected override ValueTask<PlatformResult> InvokeAsync(IPlatformIntegration a, PlatformRequest r, CancellationToken ct) => a.ExecuteAsync(r, ct);
    protected override InvocationStatus StatusOf(PlatformResult r) => r.Status;
    protected override Failure? FailureOf(PlatformResult r) => r.Failure;

    /// <summary>Name of an operation the adapter reports in its capabilities.</summary>
    protected virtual string Operation => "create-pull-request";

    protected override PlatformRequest CreateRequest(IPlatformIntegration adapter)
        => new(Context(), Operation, JsonDocument.Parse("{\"title\":\"conformance\"}").RootElement.Clone(), "conformance-key");

    [Fact]
    public void Descriptor_is_a_valid_platform_descriptor_reporting_the_operation()
    {
        var d = CreateAdapter().Descriptor;
        Assert.Empty(d.Validate());
        Assert.Equal(AdapterKind.Platform, d.Kind);
        Assert.Contains(Operation, d.Capabilities);
    }

    [Fact]
    public async Task Repeating_a_call_with_the_same_idempotency_key_does_not_repeat_the_effect()
    {
        await using var scope = new AdapterScope(CreateAdapter());
        var request = CreateRequest(scope.Adapter);
        var first = await scope.Adapter.ExecuteAsync(request, CancellationToken.None);
        var second = await scope.Adapter.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(InvocationStatus.Succeeded, second.Status);
        Assert.Equal(first.Artifacts?.Select(a => a.Id), second.Artifacts?.Select(a => a.Id));
    }

    [Fact]
    public async Task Unsupported_operation_is_a_failure_not_an_exception()
    {
        await using var scope = new AdapterScope(CreateAdapter());
        var result = await scope.Adapter.ExecuteAsync(CreateRequest(scope.Adapter) with { Operation = "no-such-operation" }, CancellationToken.None);
        Assert.Equal(InvocationStatus.Failed, result.Status);
        FailureRules.AssertNormalized(result.Failure!);
    }
}
