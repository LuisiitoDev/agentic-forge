# ADR-0014: Request sources beyond GitHub issues (Jira, Azure DevOps)

**Status:** Proposed. Not started. Depends on [ADR-0013](0013-agent-mcp-servers.md) and Phase 1 working end to end.

> **Withdrawn by [ADR-0025](0025-scope-claude-only.md):** not started and not planned. Kept as history.

## Context

A developer delegating SDLC work may track it in Jira or Azure DevOps, not only in GitHub. Today a request is
a GitHub issue labeled `forge` in the target repository ([ADR-0005](0005-request-intake.md)). The roadmap
lists other request sources as "Later".

## Decision (proposed)

1. **GitHub issues remain the request.** The engine's state machine, labels, approval and audit stay on the
   GitHub issue. This is the only source implemented today and the reference for any other.
2. **A source is an adapter that produces a GitHub issue.** For Jira or Azure DevOps, an adapter creates (or
   links) an issue in the target with the item's title, body and a back-reference, and the normal flow starts
   there. The core does not learn Jira or Azure DevOps vocabulary ([ADR-0002](0002-model-agnostic-runners.md)).
3. **Same trust rules.** The sender must be a human with write access on the target; the content of the
   external item is untrusted data; the target must be on the allowlist. The external tracker's own permissions
   do not grant approval ([ADR-0011](0011-approval-by-write-access.md)).
4. **Idempotent.** One external item maps to at most one request issue; a redelivered event does not create a
   second one ([ADR-0015](0015-reusable-workflow-intake.md)).
5. **Write-back** follows [ADR-0013](0013-agent-mcp-servers.md): by the workflow, best effort, never gating.

## Open points

- Where the adapter runs: a workflow in the engine, or a separate component. Intake has no service to host ([ADR-0016](0016-no-relay.md)).
- How Jira and Azure DevOps authenticate webhooks to the adapter (To verify).
- How the target repository is chosen for an item that does not name one (a field, a label, or a mapping in
  engine configuration).
- Whether a linked item is enough for a first version (agents read it through the MCP, a human opens the GitHub
  issue) before building automatic creation.

## Consequences

- No change to the pipeline while Phase 1 is open; this is a later phase.
- A first, cheap step exists: link an external item in the GitHub issue and let agents read it (ADR-0013).
