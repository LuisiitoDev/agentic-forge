# Governance: policy evaluation, capabilities and approvals

Governance is enforced by the runtime, not requested from agents. Every agent or tool call and every approval decision passes through one
`IPolicyEvaluator` (`AgenticForge.Application`); the default implementation is `DefaultPolicyEvaluator` (`AgenticForge.Runtime`).
Contracts live in `AgenticForge.Domain` (`Governance.cs`). Anything not explicitly allowed is denied.

## Policy evaluation

`PolicyRequest` is assembled by the engine from the pinned workflow definition, registered agent descriptors and persisted run state. It never
reads agent or tool output. `PolicyDecision` is `Allowed` plus a stable `Code` and a reason; a denial becomes a `PolicyDenied` failure.

| Action | Allowed only if | Denial codes |
|---|---|---|
| `InvokeAgent` / `InvokeTool` | every capability the agent descriptor (or tool name) needs is declared by the step; it is within the host ceiling `GovernanceOptions.AllowedCapabilities` (when set); and, for tools in `ApprovalRequiredTools`, a granted approval still covers the current material | `capability_denied`, `capability_not_granted`, `approval_required`, `approval_stale` |
| `Approve` | the approver is non-blank, not a registered agent or tool, in `GovernanceOptions.Approvers` (when set), and an `Approved` decision names the digest of the pending request | `approver_not_authorized`, `approval_digest_missing`, `approval_digest_mismatch`, `approval_decision_missing` |

Capabilities are the step's `capabilities` list in the versioned workflow; a tool's capability is its name. The host ceiling, approver
allow-list and approval-required tools are `GovernanceOptions` passed to the orchestrator by the host, never by workflows or agents, so an agent
cannot grant itself permissions or approvals: a provider's output has no path into the evaluator's inputs, and agent/tool identities are refused as approvers.

## Approval lifecycle and binding

An approval step creates an `ApprovalRequest` (stored on the waiting step run, so it survives restarts) with status `Pending`, and exactly one of
`Approved`, `Rejected`, `ChangesRequested` or `Invalidated` follows. Transitions return new immutable records; decided records are final.

`ApprovalBinding` fixes what is being approved: run id, step id, step run id, workflow id and version, `SubjectDigest` (a digest over the
`ArtifactRef` digests of the latest result of every completed step, i.e. the plan/artifact versions) and `ContextDigest` (a digest of the step input
context: run input plus completed step outputs). `Binding.Digest` hashes all of it. The approver supplies it in `ApprovalDecision.ApprovedDigest`;
approving without it, or with a different one, is refused.

- A refused decision throws `ApprovalDeniedException`, records an audit `PolicyDecided` event (with the actor) and leaves the run paused, so a correct decision can follow.
- When the decision arrives the engine recomputes the binding. If the material changed since the request, the approval is `Invalidated`, the step and run fail with `approval_invalidated`, and `ApprovalInvalidated` is recorded. The workflow must be run again (or a new approval requested) for the new material.
- Artifacts without a digest cannot be bound; the approval step fails with `approval_unbindable` instead of approving unverifiable material.
- A tool in `ApprovalRequiredTools` runs only if the run holds an `Approved` request whose subject and context digests equal the current ones. Later changes to artifacts or step outputs make the old approval `approval_stale`, and an approval from another run, step, workflow version never matches.
- Who and when: `DecidedBy` is the host-authenticated approver; `DecidedAt` uses the engine clock, not the caller's value.

## Audit

`PolicyDecided` (every denial, and every allow that depended on an approval), `ApprovalRequested`, `ApprovalDecided` and `ApprovalInvalidated` are on the
audit channel. They carry actor, action, target, decision, code and the binding digests, but never free-text reasons or step data (see observability.md).

## Threat notes and limits

- Authentication of the approver is the host's job (identity federation is out of scope); the engine only checks the identity it is given.
- The binding covers digests, not artifact bytes: adapters must verify content against `ArtifactRef.Digest` (`ArtifactDigest.Verify`).
- v1 runs one step at a time, so material can only change while paused through the store (tampering, a concurrent writer or a restored backup); the recheck at decision time covers that.
- The in-process evaluator trusts the host's `GovernanceOptions`; policy administration UIs and multi-tenant policy stores are out of scope.
