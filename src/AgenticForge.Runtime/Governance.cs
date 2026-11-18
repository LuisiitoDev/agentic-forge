using AgenticForge.Application;
using AgenticForge.Domain;

namespace AgenticForge.Runtime;

/// <summary>
/// Host-level policy knobs. They are configured by the host, never by workflow output, agents or tools.
/// </summary>
/// <param name="AllowedCapabilities">
/// Ceiling on the capabilities any step may use. Null means no ceiling beyond what each workflow declares;
/// when set, a capability not listed is denied even if a workflow declares it (least privilege, deny by default).
/// </param>
/// <param name="ApprovalRequiredTools">Tool names that may only run while a valid approval covers the current material of the run.</param>
/// <param name="Approvers">Identities allowed to decide approvals. Null means any non-automated, non-blank identity the host authenticated.</param>
public sealed record GovernanceOptions(
    IReadOnlySet<string>? AllowedCapabilities = null,
    IReadOnlySet<string>? ApprovalRequiredTools = null,
    IReadOnlySet<string>? Approvers = null);

/// <summary>The default, deterministic policy: declared capabilities, host ceiling, approval requirements and approver rules.</summary>
public sealed class DefaultPolicyEvaluator(GovernanceOptions? options = null) : IPolicyEvaluator
{
    private readonly GovernanceOptions _options = options ?? new GovernanceOptions();

    public PolicyDecision Evaluate(PolicyRequest r)
    {
        switch (r.Action)
        {
            case PolicyAction.Approve:
                return EvaluateApproval(r);

            case PolicyAction.InvokeAgent:
            case PolicyAction.InvokeTool:
                foreach (var needed in r.RequiredCapabilities)
                {
                    if (!r.DeclaredCapabilities.Contains(needed))
                        return PolicyDecision.Deny("capability_denied", r.Action == PolicyAction.InvokeAgent
                            ? $"Agent '{r.Target}' needs capability '{needed}', which step '{r.Step}' does not declare."
                            : $"Step '{r.Step}' does not declare capability '{needed}' needed to call it.");
                    if (_options.AllowedCapabilities is { } ceiling && !ceiling.Contains(needed))
                        return PolicyDecision.Deny("capability_not_granted", $"Capability '{needed}' is not granted by the host policy.");
                }
                if (r.ApprovalRequired)
                    return r.Approval switch
                    {
                        ApprovalEvidence.Valid => PolicyDecision.Allow($"'{r.Target}' is covered by a valid approval."),
                        ApprovalEvidence.Stale => PolicyDecision.Deny("approval_stale", $"'{r.Target}' requires approval, and the material changed since it was approved."),
                        _ => PolicyDecision.Deny("approval_required", $"'{r.Target}' requires an approval that has not been granted."),
                    };
                return PolicyDecision.Allow();

            default:
                return PolicyDecision.Deny("unknown_action", "Unknown policy action.");
        }
    }

    private PolicyDecision EvaluateApproval(PolicyRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Principal))
            return PolicyDecision.Deny("approver_not_authorized", "An approval needs an authenticated approver identity.");
        if (r.PrincipalIsAutomated)
            return PolicyDecision.Deny("approver_not_authorized", "Agents and tools cannot approve; approval comes from an authenticated human.");
        if (_options.Approvers is { } allowed && !allowed.Contains(r.Principal))
            return PolicyDecision.Deny("approver_not_authorized", "The approver is not authorized to decide approvals.");
        if (r.Decision is null)
            return PolicyDecision.Deny("approval_decision_missing", "The approval request names no decision.");
        if (r.Decision is not ApprovalDecisionKind.Approved)
            return PolicyDecision.Allow("Only an approval needs to match the material; this decision grants nothing.");
        if (string.IsNullOrWhiteSpace(r.PresentedDigest))
            return PolicyDecision.Deny("approval_digest_missing", "An approval must name the digest of the material it approves.");
        if (!string.Equals(r.PresentedDigest, r.ExpectedDigest, StringComparison.OrdinalIgnoreCase))
            return PolicyDecision.Deny("approval_digest_mismatch", "The approval does not match the material awaiting approval.");
        return PolicyDecision.Allow("Approval matches the pending request.");
    }
}

/// <summary>
/// Raised by <see cref="WorkflowOrchestrator.ResumeAsync"/> when an approval decision is refused. The run stays paused and
/// the refusal is recorded as an audit event, so a rejected attempt can be retried with a correct decision.
/// </summary>
public sealed class ApprovalDeniedException(PolicyDecision decision)
    : InvalidOperationException($"Approval denied ({decision.Code}): {decision.Reason}")
{
    public PolicyDecision Decision { get; } = decision;
}
