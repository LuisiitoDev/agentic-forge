# ADR-0025: Multi-provider runners and the unstarted proposals are dropped

**Status:** Accepted. Withdraws [ADR-0004](0004-runtime-as-future-engine.md), [ADR-0013](0013-agent-mcp-servers.md) and
[ADR-0014](0014-external-request-sources.md). Refines [ADR-0002](0002-model-agnostic-runners.md). Follows
[ADR-0024](0024-claude-code-only.md).

## Context

With one engine ([ADR-0024](0024-claude-code-only.md)), some documents and proposals described work nobody has started and
nobody has asked for.

## Decision

1. **Runners for other providers are dropped.** `runner-contract.md` (the contract a model adapter had to meet, with
   a capability table for Claude, Codex and Copilot), Phases 3 and 4 of the roadmap and the provider labels
   (`forge:claude`, `forge:codex`, `forge:copilot`, which the intake never read) are removed. ADR-0002 stays as history.
2. **Withdrawn ADRs** (not started, and not planned):
   - [ADR-0004](0004-runtime-as-future-engine.md): a runtime on Azure as the engine. The engine stays on GitHub Actions.
   - [ADR-0013](0013-agent-mcp-servers.md): MCP servers for the agents. No server was ever declared, it was built on
     `gh aw`, and the agents now read the request from files.
   - [ADR-0014](0014-external-request-sources.md): request sources beyond GitHub issues. It depended on ADR-0013.
3. **Phase 5 of the roadmap and the "Later" list are removed.** Quotas, usage visibility and a secret rotation guide
   stay as ideas with no date.
4. **`handoff-contract.md` is deleted**: a snapshot of 2026-11-19 with test counts and a task list that no longer
   hold. `docs/roadmap.md`, `AGENTS.md` and the "To verify" lists of the ADRs say where the project stands.

## Consequences

- The documents describe what exists. A new provider, a service or another request source would be a new ADR.
- The ADR records stay in the repository (they explain why, and ADRs are not deleted); the three withdrawn ones say so.
