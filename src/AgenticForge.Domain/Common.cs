using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticForge.Domain;

/// <summary>Shared JSON settings for all contracts. Properties are camelCase; enums are PascalCase strings; nulls are omitted.</summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create() => new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}

/// <summary>Current schema version of serialized contracts. See docs/design/compatibility-and-versioning.md.</summary>
public static class ContractSchema
{
    public const int Current = 1;
}

/// <summary>Why work did not succeed. Cancellation is NOT a failure; see the Cancelled statuses.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FailureKind
{
    /// <summary>Input or output did not satisfy its schema or a workflow rule.</summary>
    Validation,
    /// <summary>The agent, tool or execution provider reported an error.</summary>
    Provider,
    /// <summary>A step exceeded its deadline.</summary>
    Timeout,
    /// <summary>A policy or capability boundary rejected the action.</summary>
    PolicyDenied,
    /// <summary>A defect or unexpected condition inside Agentic Forge.</summary>
    Internal,
}

/// <summary>An explicit, serializable failure. Never carries provider exception types or secrets.</summary>
public sealed record Failure(
    FailureKind Kind,
    string Code,
    string Message,
    bool Retryable = false,
    IReadOnlyDictionary<string, string>? Details = null);

/// <summary>Durable, addressable reference to a produced or consumed artifact.</summary>
/// <param name="Kind">Free-form category such as "text", "json", "file", "pull-request".</param>
/// <param name="Uri">Location understood by the adapter that owns the artifact; opaque to the core.</param>
public sealed record ArtifactRef(
    ArtifactId Id,
    string Kind,
    string Uri,
    string? MediaType = null,
    string? Digest = null);

/// <summary>
/// Identifiers that link every event, result and log line of one run. Supplied by the runtime,
/// never invented by agents or tools.
/// </summary>
/// <param name="CorrelationId">Stable across the whole run (and any external request that started it).</param>
/// <param name="CausationId">Id of the command/event that caused this unit of work, if any.</param>
public sealed record Correlation(
    WorkflowRunId RunId,
    string CorrelationId,
    StepRunId? StepRunId = null,
    string? CausationId = null);

/// <summary>Outcome of a human approval. The identity comes from the host's auth port, not from an agent.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalDecisionKind { Approved, Rejected, ChangesRequested }

/// <param name="ApprovedDigest">
/// The <see cref="ApprovalBinding.Digest"/> the approver reviewed. An <see cref="ApprovalDecisionKind.Approved"/> decision without a
/// digest, or with one that does not match the pending request, is denied (see docs/design/governance.md).
/// </param>
public sealed record ApprovalDecision(
    string Approver,
    ApprovalDecisionKind Decision,
    DateTimeOffset DecidedAt,
    string? Reason = null,
    string? ApprovedDigest = null);
