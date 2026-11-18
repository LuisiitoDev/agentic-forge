using System.Text.Json;
using AgenticForge.Abstractions;
using AgenticForge.Domain;

namespace AgenticForge.Adapters.Conformance.Fakes;

public enum FakeMode
{
    /// <summary>Succeeds.</summary>
    Normal,
    /// <summary>Fails with a classified, retryable provider error (HTTP 429 style) carrying diagnostics.</summary>
    RateLimited,
    /// <summary>Never completes unless cancelled.</summary>
    Block,
}

internal static class FakeSupport
{
    public static Failure RateLimit(string adapter) => ProviderFailures.ToFailure(new ProviderError(
        adapter, ProviderErrorCategory.RateLimited, "429", "Rate limit exceeded", TimeSpan.FromSeconds(5),
        new Dictionary<string, string> { ["requestId"] = "req-1", ["apiKey"] = "sk-must-not-leak" }));

    public static Failure NotFound(string adapter, string what) => ProviderFailures.ToFailure(
        new ProviderError(adapter, ProviderErrorCategory.NotFound, "404", $"{what} is not known to this adapter."));

    public static async ValueTask<bool> BlockUntilCancelledAsync(CancellationToken ct)
    {
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { return true; }
        return false;
    }

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

/// <summary>Counts lifecycle calls so tests can see the registry honored the contract.</summary>
public sealed class LifecycleProbe : IAdapterLifecycle
{
    public int Initialized { get; private set; }
    public int Disposed { get; private set; }
    public bool FailInitialize { get; init; }

    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (FailInitialize) throw new InvalidOperationException("boom");
        Initialized++;
        return ValueTask.CompletedTask;
    }
    public ValueTask<AdapterHealth> CheckHealthAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(new AdapterHealth(Initialized > 0 && Disposed == 0));
    public ValueTask DisposeAsync() { Disposed++; return ValueTask.CompletedTask; }
}

public sealed class FakeModelInference(string id = "fake-model", FakeMode mode = FakeMode.Normal, string reply = "hello") : IModelInference, IAdapterLifecycle
{
    private readonly LifecycleProbe _lifecycle = new();
    public AdapterDescriptor Descriptor { get; } = new(id, AdapterKind.ModelInference, "1.0.0", ["chat"]);
    public List<ModelRequest> Calls { get; } = [];

    public async ValueTask<ModelResult> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        if (cancellationToken.IsCancellationRequested) return ModelResult.CancelledResult();
        switch (mode)
        {
            case FakeMode.RateLimited: return ModelResult.Fault(FakeSupport.RateLimit(id));
            case FakeMode.Block:
                await FakeSupport.BlockUntilCancelledAsync(cancellationToken);
                return ModelResult.CancelledResult();
            default: return ModelResult.Success(reply, new ModelUsage(request.Messages.Count, 1));
        }
    }

    public ValueTask InitializeAsync(CancellationToken ct) => _lifecycle.InitializeAsync(ct);
    public ValueTask<AdapterHealth> CheckHealthAsync(CancellationToken ct) => _lifecycle.CheckHealthAsync(ct);
    public ValueTask DisposeAsync() => _lifecycle.DisposeAsync();
}

/// <summary>Reference agent with canned behavior. Independent of any model: the first implementation of the agent port.</summary>
public sealed class FakeAgent(string id = "fake-agent", FakeMode mode = FakeMode.Normal, string outcome = "done") : IAgent
{
    public AgentDescriptor Descriptor { get; } = new(new AgentId(id), id, ["read-requirements"]);
    public List<AgentInvocationRequest> Calls { get; } = [];

    public async ValueTask<AgentInvocationResult> InvokeAsync(AgentInvocationRequest request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        if (cancellationToken.IsCancellationRequested) return AgentInvocationResult.CancelledResult();
        switch (mode)
        {
            case FakeMode.RateLimited: return AgentInvocationResult.Fault(FakeSupport.RateLimit(id));
            case FakeMode.Block:
                await FakeSupport.BlockUntilCancelledAsync(cancellationToken);
                return AgentInvocationResult.CancelledResult();
            default: return AgentInvocationResult.Success(outcome, FakeSupport.Json("{\"by\":\"" + id + "\"}"));
        }
    }
}

/// <summary>
/// Second implementation of the agent port: delegates to a <see cref="IModelInference"/> adapter. Shows how an agent is built on
/// the model port, and that it can replace <see cref="FakeAgent"/> under the same workflow without changing the core.
/// </summary>
public sealed class ModelBackedAgent(string id, IModelInference model, string outcome = "done") : IAgent
{
    public AgentDescriptor Descriptor { get; } = new(new AgentId(id), id, ["read-requirements"]);

    public async ValueTask<AgentInvocationResult> InvokeAsync(AgentInvocationRequest request, CancellationToken cancellationToken)
    {
        var result = await model.CompleteAsync(
            new ModelRequest(request.Context, "default", [new ModelMessage("user", request.Input.GetRawText())]), cancellationToken);
        return result.Status switch
        {
            InvocationStatus.Succeeded => AgentInvocationResult.Success(outcome, FakeSupport.Json("{\"by\":\"" + id + "\"}")),
            InvocationStatus.Cancelled => AgentInvocationResult.CancelledResult(),
            _ => AgentInvocationResult.Fault(result.Failure!), // provider details survive untouched
        };
    }
}

public sealed class FakeToolProvider(FakeMode mode = FakeMode.Normal) : IToolProvider
{
    public IReadOnlyList<ToolDescriptor> Tools { get; } = [new(new ToolName("echo"), "Echoes its arguments", ToolSideEffects.None)];
    public int Calls { get; private set; }

    public async ValueTask<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        if (cancellationToken.IsCancellationRequested) return ToolInvocationResult.CancelledResult();
        if (request.Tool.Value != "echo")
            return ToolInvocationResult.Fault(FakeSupport.NotFound("fake-tools", $"Tool '{request.Tool}'"));
        switch (mode)
        {
            case FakeMode.RateLimited: return ToolInvocationResult.Fault(FakeSupport.RateLimit("fake-tools"));
            case FakeMode.Block:
                await FakeSupport.BlockUntilCancelledAsync(cancellationToken);
                return ToolInvocationResult.CancelledResult();
            default: return ToolInvocationResult.Success(request.Arguments);
        }
    }
}

public sealed class FakeExecutionEnvironment(string id = "fake-exec", FakeMode mode = FakeMode.Normal) : IExecutionEnvironment
{
    public AdapterDescriptor Descriptor { get; } = new(id, AdapterKind.ExecutionEnvironment, "1.0.0", ["run-command"]);

    public async ValueTask<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return ExecutionResult.CancelledResult();
        switch (mode)
        {
            case FakeMode.RateLimited: return ExecutionResult.Fault(FakeSupport.RateLimit(id));
            case FakeMode.Block:
                await FakeSupport.BlockUntilCancelledAsync(cancellationToken);
                return ExecutionResult.CancelledResult();
            default:
                // A failing command is a normal result (non-zero exit), not an adapter failure.
                return request.Command == "false"
                    ? ExecutionResult.Completed(1, stderr: "failed")
                    : ExecutionResult.Completed(0, string.Join(' ', request.Arguments ?? []));
        }
    }
}

public sealed class FakePlatformIntegration(string id = "fake-platform", FakeMode mode = FakeMode.Normal) : IPlatformIntegration
{
    private readonly Dictionary<string, PlatformResult> _byKey = [];
    public AdapterDescriptor Descriptor { get; } = new(id, AdapterKind.Platform, "1.0.0", ["create-pull-request"]);
    /// <summary>How many external effects actually happened.</summary>
    public int Effects { get; private set; }

    public async ValueTask<PlatformResult> ExecuteAsync(PlatformRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return PlatformResult.CancelledResult();
        if (!Descriptor.Capabilities.Contains(request.Operation))
            return PlatformResult.Fault(FakeSupport.NotFound(id, $"Operation '{request.Operation}'"));
        switch (mode)
        {
            case FakeMode.RateLimited: return PlatformResult.Fault(FakeSupport.RateLimit(id));
            case FakeMode.Block:
                await FakeSupport.BlockUntilCancelledAsync(cancellationToken);
                return PlatformResult.CancelledResult();
        }
        if (request.IdempotencyKey is { } key && _byKey.TryGetValue(key, out var previous)) return previous;
        Effects++;
        var result = PlatformResult.Success(null, [new ArtifactRef(new ArtifactId($"pr-{Effects}"), "pull-request", $"fake://pr/{Effects}")]);
        if (request.IdempotencyKey is { } k) _byKey[k] = result;
        return result;
    }
}
