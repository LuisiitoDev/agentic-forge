using System.Text;
using AgenticForge.Application;
using AgenticForge.Domain;

namespace AgenticForge.Runtime;

// Governance: policy evaluation before every invocation, and approval binding, validation and invalidation.
// Design and threat notes: docs/design/governance.md.
public sealed partial class WorkflowOrchestrator
{
    /// <summary>
    /// Evaluates the central policy for an agent or tool call. Denials, and allows that depended on an approval, are recorded
    /// as audit events. The evaluator is given only definition-derived data, never agent or tool output.
    /// </summary>
    private async ValueTask<PolicyDecision> AuthorizeInvocationAsync(WorkflowRun run, StepDefinition stepDef, StepRun step,
        PolicyAction action, string target, IReadOnlyList<string> required)
    {
        var approvalRequired = action == PolicyAction.InvokeTool && _governance.ApprovalRequiredTools?.Contains(target) == true;
        var evidence = approvalRequired ? await FindApprovalEvidenceAsync(run, stepDef) : ApprovalEvidence.NotRequired;
        var r = run.State;
        var decision = _policy.Evaluate(new PolicyRequest(r.Id, r.WorkflowId, r.WorkflowVersion, stepDef.Id, action, target,
            stepDef.Capabilities ?? [], required, ApprovalRequired: approvalRequired, Approval: evidence));
        if (!decision.Allowed || approvalRequired)
            await EmitPolicyAsync(run, step, action, target, decision);
        return decision;
    }

    private async ValueTask<ApprovalEvidence> FindApprovalEvidenceAsync(WorkflowRun run, StepDefinition stepDef)
    {
        var granted = (await _store.GetStepRunsAsync(run.Id, CancellationToken.None))
            .Select(s => s.ApprovalRequest)
            .Where(a => a is { Status: ApprovalStatus.Approved })
            .Select(a => a!.Binding)
            .ToList();
        if (granted.Count == 0) return ApprovalEvidence.Missing;
        var current = await BindApprovalAsync(run, stepDef, null);
        return current is not null && granted.Any(g => g.Covers(current)) ? ApprovalEvidence.Valid : ApprovalEvidence.Stale;
    }

    /// <summary>
    /// Validates an approval decision against the pending request. Returns the decided request to store, or a final run snapshot
    /// when the request was invalidated. A refused decision throws <see cref="ApprovalDeniedException"/> and leaves the run paused.
    /// </summary>
    private async ValueTask<(ApprovalRequest? Decided, WorkflowRunSnapshot? Final)> AuthorizeDecisionAsync(WorkflowRun run,
        StepDefinition stepDef, StepRun step, ApprovalDecision decision)
    {
        var request = step.State.ApprovalRequest;
        if (request is null)
        {
            var missing = PolicyDecision.Deny("approval_request_missing", "The waiting step has no approval request to decide.");
            await EmitPolicyAsync(run, step, PolicyAction.Approve, stepDef.Id.Value, missing, decision.Approver);
            throw new ApprovalDeniedException(missing);
        }

        var current = await BindApprovalAsync(run, stepDef, step.Id);
        if (current is null || current.Digest != request.Binding.Digest)
        {
            var failure = new Failure(FailureKind.PolicyDenied, "approval_invalidated",
                "The material under review changed after approval was requested; the approval is void.");
            step.InvalidateApproval(Now, failure, failure.Code);
            await SaveAsync(step);
            await EmitAsync(run, ExecutionEventKind.ApprovalInvalidated, step, failure: failure, attributes: BindingAttributes(request.Binding));
            await EmitStepEndAsync(run, step);
            return (null, await FailRunAsync(run, failure));
        }

        var r = run.State;
        var automated = !string.IsNullOrWhiteSpace(decision.Approver) && decision.Approver is var a
                        && (_agents.Resolve(new AgentId(a)) is not null || _tools.Resolve(new ToolName(a)) is not null);
        var verdict = _policy.Evaluate(new PolicyRequest(r.Id, r.WorkflowId, r.WorkflowVersion, stepDef.Id, PolicyAction.Approve,
            stepDef.Id.Value, [], [], decision.Approver, automated, PresentedDigest: decision.ApprovedDigest,
            ExpectedDigest: request.Binding.Digest, Decision: decision.Decision));
        if (!verdict.Allowed)
        {
            await EmitPolicyAsync(run, step, PolicyAction.Approve, stepDef.Id.Value, verdict, decision.Approver);
            throw new ApprovalDeniedException(verdict);
        }
        return (request.Decide(decision.Approver, decision.Decision, Now), null);
    }

    /// <summary>
    /// Binds an approval to the run, step, workflow version, the digests of the latest artifacts of every completed step, and the
    /// step input context. Returns null if any artifact lacks a digest (unverifiable material cannot be approved).
    /// </summary>
    private async ValueTask<ApprovalBinding?> BindApprovalAsync(WorkflowRun run, StepDefinition stepDef, StepRunId? stepRun)
    {
        var artifacts = (await _store.GetStepRunsAsync(run.Id, CancellationToken.None))
            .Where(s => s.Status == StepRunStatus.Succeeded)
            .GroupBy(s => s.StepId)
            .SelectMany(g => g.Last().Artifacts ?? []);
        if (ApprovalBinding.SubjectDigestOf(artifacts) is not { } subject) return null;
        var context = ArtifactDigest.Compute(Encoding.UTF8.GetBytes((await BuildInputAsync(run)).GetRawText()));
        var r = run.State;
        return new ApprovalBinding(r.Id, stepDef.Id, stepRun, r.WorkflowId, r.WorkflowVersion, subject, context);
    }

    private static Dictionary<string, string> BindingAttributes(ApprovalBinding b) => new()
    {
        ["bindingDigest"] = b.Digest,
        ["subjectDigest"] = b.SubjectDigest,
        ["contextDigest"] = b.ContextDigest,
    };

    private ValueTask EmitPolicyAsync(WorkflowRun run, StepRun step, PolicyAction action, string target, PolicyDecision decision,
        string? actor = null)
        => EmitAsync(run, ExecutionEventKind.PolicyDecided, step, actor: actor, attributes: new Dictionary<string, string>
        {
            ["action"] = action.ToString(),
            ["target"] = target,
            ["decision"] = decision.Allowed ? "Allow" : "Deny",
            ["code"] = decision.Code,
        });
}
