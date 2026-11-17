using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticForge.Domain;

namespace AgenticForge.Abstractions;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolSideEffects
{
    /// <summary>Read-only.</summary>
    None,
    /// <summary>Writes, but repeating the call with the same idempotency key has no extra effect.</summary>
    Idempotent,
    /// <summary>Repeating the call may repeat the effect; retries need care.</summary>
    NonIdempotent,
}

/// <summary>Schema-described capability. Schemas are JSON Schema documents, opaque to the core.</summary>
public sealed record ToolDescriptor(
    ToolName Name,
    string Description,
    ToolSideEffects SideEffects,
    JsonElement? InputSchema = null,
    JsonElement? OutputSchema = null);

public sealed record ToolInvocationRequest(
    ToolName Tool,
    StepExecutionContext Context,
    JsonElement Arguments,
    string? IdempotencyKey = null);

public sealed record ToolInvocationResult
{
    public InvocationStatus Status { get; init; }
    public JsonElement? Output { get; init; }
    public Failure? Failure { get; init; }
    public IReadOnlyList<ArtifactRef>? Artifacts { get; init; }

    public static ToolInvocationResult Success(JsonElement? output = null, IReadOnlyList<ArtifactRef>? artifacts = null)
        => new() { Status = InvocationStatus.Succeeded, Output = output, Artifacts = artifacts };

    public static ToolInvocationResult Fault(Failure failure)
        => new() { Status = InvocationStatus.Failed, Failure = failure };

    public static ToolInvocationResult CancelledResult()
        => new() { Status = InvocationStatus.Cancelled };
}

/// <summary>
/// A source of tools (port). Authentication, tool policy and side-effect handling are the adapter's job;
/// the runtime only checks that the step's declared capabilities permit the call.
/// </summary>
public interface IToolProvider
{
    IReadOnlyList<ToolDescriptor> Tools { get; }
    ValueTask<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken cancellationToken);
}
