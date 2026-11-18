using System.Text.Json.Serialization;

namespace AgenticForge.Domain;

/// <summary>
/// Which stream an event belongs to. <see cref="Audit"/> events are durable evidence of decisions and results and must be kept;
/// <see cref="Telemetry"/> events are operational detail that may be sampled, dropped or expired.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EventChannel { Telemetry, Audit }

/// <summary>Lifecycle facts the engine records. Names are provider-neutral; adding a kind is a compatible change.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionEventKind
{
    RunStarted,
    RunPaused,
    RunResumed,
    RunSucceeded,
    RunFailed,
    RunCancelled,
    /// <summary>Recovery took over a run that was interrupted by a restart or crash and continued it.</summary>
    RunRecovered,
    StepStarted,
    StepAwaitingInput,
    StepAwaitingApproval,
    StepSucceeded,
    StepFailed,
    StepCancelled,
    /// <summary>A retryable step failure will be followed by a new attempt (a new step run).</summary>
    StepRetryScheduled,
    AgentInvoked,
    AgentCompleted,
    ToolInvoked,
    ToolCompleted,
    ApprovalRequested,
    ApprovalDecided,
    /// <summary>A policy evaluation denied an action, or allowed one that depended on an approval.</summary>
    PolicyDecided,
    /// <summary>A pending or granted approval no longer covers the material it was bound to.</summary>
    ApprovalInvalidated,
}

/// <summary>Maps each kind to its default channel: what happened and who decided is audit; call-level detail is telemetry.</summary>
public static class ExecutionEventKinds
{
    public static EventChannel ChannelOf(ExecutionEventKind kind) => kind switch
    {
        ExecutionEventKind.AgentInvoked or ExecutionEventKind.AgentCompleted
            or ExecutionEventKind.ToolInvoked or ExecutionEventKind.ToolCompleted
            or ExecutionEventKind.StepRetryScheduled or ExecutionEventKind.StepAwaitingInput => EventChannel.Telemetry,
        _ => EventChannel.Audit,
    };
}

/// <summary>
/// One immutable, versioned, provider-neutral fact about a run. Events carry identifiers, outcomes, redacted failures and
/// artifact references only; they never carry step input/output, prompts or credentials (see docs/design/observability.md).
/// </summary>
/// <param name="Sequence">Position within the run, starting at 1. Assigned by the event sink on append (0 before append).</param>
/// <param name="WorkflowVersion">The pinned workflow version; step definitions are versioned with their workflow.</param>
/// <param name="CorrelationId">Same value as <see cref="WorkflowRunSnapshot.CorrelationId"/>; links the event to the originating request.</param>
/// <param name="Actor">Who made a decision (approver identity for approvals); null for engine-driven events.</param>
/// <param name="Outcome">Step outcome or approval decision name, where applicable.</param>
/// <param name="Artifacts">References (with digests) to artifacts produced; the content itself is stored elsewhere.</param>
/// <param name="Attributes">Small, non-sensitive key/values, e.g. the agent id or tool name. Redacted by the engine.</param>
public sealed record ExecutionEvent(
    string EventId,
    long Sequence,
    DateTimeOffset Timestamp,
    ExecutionEventKind Kind,
    EventChannel Channel,
    WorkflowRunId RunId,
    string CorrelationId,
    WorkflowId WorkflowId,
    WorkflowVersion WorkflowVersion,
    StepId? StepId = null,
    StepRunId? StepRunId = null,
    int? Attempt = null,
    string? Outcome = null,
    Failure? Failure = null,
    string? Actor = null,
    IReadOnlyList<ArtifactRef>? Artifacts = null,
    IReadOnlyDictionary<string, string>? Attributes = null,
    int SchemaVersion = ContractSchema.Current);
