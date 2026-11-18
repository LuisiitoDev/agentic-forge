using System.Text;
using System.Text.Json.Serialization;

namespace AgenticForge.Domain;

/// <summary>What a policy evaluation is asked to authorize.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PolicyAction { InvokeAgent, InvokeTool, Approve }

/// <summary>Whether an approval that a policy requires exists for the material about to be acted on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalEvidence
{
    /// <summary>No approval was required, so none was looked up.</summary>
    NotRequired,
    /// <summary>Required, and no granted approval exists for this run.</summary>
    Missing,
    /// <summary>Required; a granted approval exists but its binding no longer matches the current material.</summary>
    Stale,
    /// <summary>Required, and a granted approval is bound to exactly the current material.</summary>
    Valid,
}

/// <summary>
/// Everything a policy may look at. The request is assembled by the engine from the pinned workflow definition, registered
/// descriptors and persisted state, never from agent or tool output, so a provider cannot widen its own permissions.
/// </summary>
/// <param name="DeclaredCapabilities">What the step declares in the (versioned, immutable) workflow definition.</param>
/// <param name="RequiredCapabilities">What the agent descriptor or tool call needs. Anything not declared is denied.</param>
/// <param name="Principal">For <see cref="PolicyAction.Approve"/>: the identity that supplied the decision.</param>
/// <param name="PrincipalIsAutomated">True when the principal is a registered agent or tool. Automation can never approve.</param>
/// <param name="ApprovalRequired">The host policy requires a valid approval before this action runs.</param>
/// <param name="PresentedDigest">For Approve: the binding digest the approver says they reviewed.</param>
/// <param name="ExpectedDigest">For Approve: the binding digest of the pending request.</param>
/// <param name="Decision">For Approve: the decision kind. Only an Approved decision must name the digest; rejecting grants nothing.</param>
public sealed record PolicyRequest(
    WorkflowRunId Run,
    WorkflowId Workflow,
    WorkflowVersion Version,
    StepId Step,
    PolicyAction Action,
    string Target,
    IReadOnlyList<string> DeclaredCapabilities,
    IReadOnlyList<string> RequiredCapabilities,
    string? Principal = null,
    bool PrincipalIsAutomated = false,
    bool ApprovalRequired = false,
    ApprovalEvidence Approval = ApprovalEvidence.NotRequired,
    string? PresentedDigest = null,
    string? ExpectedDigest = null,
    ApprovalDecisionKind? Decision = null);

/// <summary>Result of a policy evaluation. Only an explicit <see cref="Allowed"/> permits the action; there is no "unknown".</summary>
public sealed record PolicyDecision(bool Allowed, string Code, string Reason)
{
    public static PolicyDecision Allow(string reason = "Allowed.") => new(true, "allowed", reason);
    public static PolicyDecision Deny(string code, string reason) => new(false, code, reason);

    public Failure? ToFailure() => Allowed ? null : new Failure(FailureKind.PolicyDenied, Code, Reason);
}

/// <summary>
/// The exact material an approval authorizes. Changing any component changes <see cref="Digest"/>, so an approval for one
/// run, step, workflow version, set of artifact digests or context can never be reused for another.
/// </summary>
/// <param name="SubjectDigest">Digest over the digests of the artifacts under review (the plan/result versions).</param>
/// <param name="ContextDigest">Digest of the step input context (run input plus completed step outputs) at the time of the request.</param>
public sealed record ApprovalBinding(
    WorkflowRunId Run,
    StepId Step,
    StepRunId? StepRun,
    WorkflowId Workflow,
    WorkflowVersion Version,
    string SubjectDigest,
    string ContextDigest)
{
    /// <summary>Digest of the whole binding, including the step run. This is the value an approver signs off on.</summary>
    public string Digest => ArtifactDigest.Compute(Encoding.UTF8.GetBytes(
        $"agentic-forge.approval.v1\nrun={Run}\nstep={Step}\nstepRun={StepRun}\nworkflow={Workflow}@{Version}\nsubject={SubjectDigest}\ncontext={ContextDigest}"));

    /// <summary>True if an approval granted for this binding still covers the material of <paramref name="current"/>, in any step run.</summary>
    public bool Covers(ApprovalBinding current)
        => Run == current.Run && Workflow == current.Workflow && Version == current.Version
           && SubjectDigest == current.SubjectDigest && ContextDigest == current.ContextDigest;

    /// <summary>
    /// Digest over artifact references, order-independent. Null when any artifact has no digest: unverifiable material
    /// cannot be bound, so callers must deny rather than approve it.
    /// </summary>
    public static string? SubjectDigestOf(IEnumerable<ArtifactRef> artifacts)
    {
        var lines = new List<string>();
        foreach (var a in artifacts)
        {
            if (string.IsNullOrWhiteSpace(a.Digest)) return null;
            lines.Add($"{a.Id}={a.Digest.ToLowerInvariant()}");
        }
        lines.Sort(StringComparer.Ordinal);
        return ArtifactDigest.Compute(Encoding.UTF8.GetBytes("agentic-forge.subject.v1\n" + string.Join('\n', lines)));
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalStatus { Pending, Approved, Rejected, ChangesRequested, Invalidated }

/// <summary>
/// Lifecycle record of one approval: Pending, then exactly one of Approved, Rejected, ChangesRequested or Invalidated.
/// Immutable; every transition returns a new record. Decided and invalidated records are final.
/// </summary>
/// <param name="DecidedBy">Who decided (the approver identity from the host's auth port). Null while pending and when invalidated.</param>
public sealed record ApprovalRequest(
    ApprovalBinding Binding,
    DateTimeOffset RequestedAt,
    ApprovalStatus Status = ApprovalStatus.Pending,
    string? DecidedBy = null,
    DateTimeOffset? DecidedAt = null,
    string? InvalidationReason = null)
{
    public static ApprovalRequest Request(ApprovalBinding binding, DateTimeOffset now) => new(binding, now);

    /// <summary>Records a decision, stamped with the engine's clock rather than the caller's.</summary>
    public ApprovalRequest Decide(string approver, ApprovalDecisionKind decision, DateTimeOffset now)
    {
        Require(ApprovalStatus.Pending, "decide");
        return this with
        {
            Status = decision switch
            {
                ApprovalDecisionKind.Approved => ApprovalStatus.Approved,
                ApprovalDecisionKind.Rejected => ApprovalStatus.Rejected,
                ApprovalDecisionKind.ChangesRequested => ApprovalStatus.ChangesRequested,
                _ => throw new ArgumentOutOfRangeException(nameof(decision)),
            },
            DecidedBy = approver,
            DecidedAt = now,
        };
    }

    /// <summary>Withdraws a pending or granted approval because the approved material changed.</summary>
    public ApprovalRequest Invalidate(DateTimeOffset now, string reason)
    {
        if (Status is ApprovalStatus.Invalidated or ApprovalStatus.Rejected or ApprovalStatus.ChangesRequested)
            throw new InvalidLifecycleTransitionException(nameof(ApprovalRequest), Status.ToString(), "invalidate");
        return this with { Status = ApprovalStatus.Invalidated, DecidedAt = now, InvalidationReason = reason };
    }

    private void Require(ApprovalStatus expected, string op)
    {
        if (Status != expected) throw new InvalidLifecycleTransitionException(nameof(ApprovalRequest), Status.ToString(), op);
    }
}
