using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticForge.Domain;

namespace AgenticForge.Abstractions;

/// <summary>Per-invocation facts the runtime hands to an agent or tool. Not an ambient/static context.</summary>
/// <param name="Attempt">1-based attempt number of the owning StepRun.</param>
/// <param name="Deadline">Absolute deadline, if the step declares a timeout. Cooperative; see the cancellation token.</param>
public sealed record StepExecutionContext(Correlation Correlation, int Attempt = 1, DateTimeOffset? Deadline = null);

/// <summary>Self-description of an agent implementation. Capability names are matched against step policy.</summary>
public sealed record AgentDescriptor(AgentId Id, string DisplayName, IReadOnlyList<string> Capabilities);

public sealed record AgentInvocationRequest(
    AgentId Agent,
    StepExecutionContext Context,
    JsonElement Input,
    IReadOnlyList<ArtifactRef>? InputArtifacts = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InvocationStatus
{
    Succeeded,
    Failed,
    /// <summary>The agent honored cancellation. Not a failure.</summary>
    Cancelled,
    /// <summary>The agent needs more input; the output describes what is missing. Pauses the StepRun.</summary>
    NeedsInput,
}

/// <summary>
/// Result of one agent invocation. Exactly one shape per status; use the factories to build it.
/// An agent reports an <see cref="Outcome"/> but never chooses the next step: routing belongs to the workflow.
/// </summary>
public sealed record AgentInvocationResult
{
    public InvocationStatus Status { get; init; }
    public string? Outcome { get; init; }
    public JsonElement? Output { get; init; }
    public Failure? Failure { get; init; }
    public IReadOnlyList<ArtifactRef>? Artifacts { get; init; }

    public static AgentInvocationResult Success(string outcome, JsonElement? output = null, IReadOnlyList<ArtifactRef>? artifacts = null)
        => new() { Status = InvocationStatus.Succeeded, Outcome = outcome, Output = output, Artifacts = artifacts };

    public static AgentInvocationResult Fault(Failure failure)
        => new() { Status = InvocationStatus.Failed, Failure = failure };

    public static AgentInvocationResult CancelledResult()
        => new() { Status = InvocationStatus.Cancelled };

    public static AgentInvocationResult NeedsMoreInput(JsonElement questions)
        => new() { Status = InvocationStatus.NeedsInput, Output = questions };
}

/// <summary>
/// An agent implementation (port). Invoking an agent performs ONE unit of work for ONE step; it must not
/// schedule other steps or mutate run state. Implementations should observe the cancellation token
/// and return <see cref="AgentInvocationResult.CancelledResult"/> (throwing <see cref="OperationCanceledException"/>
/// is also treated as cancellation). Failures are returned, not thrown; unexpected exceptions are Internal failures.
/// </summary>
public interface IAgent
{
    AgentDescriptor Descriptor { get; }
    ValueTask<AgentInvocationResult> InvokeAsync(AgentInvocationRequest request, CancellationToken cancellationToken);
}
