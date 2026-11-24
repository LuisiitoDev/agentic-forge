# Roadmap

Documentation first, then code. Each phase is shippable and verified before the next starts.

## Phase 0 — Documentation (current)

- Vision, request contract, architecture, agent spec, target repositories, security, ADRs.
- Resolve the open questions in [vision and scope](vision-and-scope.md#open-questions).
- Verify the open **To verify** items: App permissions, dispatch permission, re-run behaviour of the intake.

**Exit:** the documents are reviewed and agreed.

## Phase 1 — Engine skeleton with one target

- GitHub App used only for short-lived tokens to act on targets (no webhook), a reusable intake workflow called
  by each target, and `repository_dispatch` into the engine repo ([ADR-0015](decisions/0015-reusable-workflow-intake.md)).
- Allowlist, sender-authority checks, loop prevention and idempotent stages.
- The label-driven pipeline and the shared agent workflow, working against one target repository.
- Agents on Claude Code inside AWF ([ADR-0022](decisions/0022-all-agents-on-claude-in-awf.md), [ADR-0024](decisions/0024-claude-code-only.md)), with provider-neutral specs for the four agents (BA, Planner, Coder, Reviewer; the Infra agent was removed by [ADR-0023](decisions/0023-no-infra-agent.md)).

**Checked on real GitHub (2026-11-23 and 24):** each agent on its own and then the whole chain orchestrated on a sandbox
repository, with the intake, the orchestrator's wait for the runs ([ADR-0018](decisions/0018-orchestrator-awaits-agent-runs.md)),
the human approval and the delivery job: BA, Planner, approval, Coder, Reviewer `pass`. The firewall was probed: only the
Anthropic API is reachable from the agent's commands.

**Not verified:** the Reviewer reading CI results and alerts with `gh` or `curl` inside AWF (the sandbox has no CI), the
Planner's `Out of scope (a person does it)` section for a request that needs infrastructure, and the terms under which
a subscription token may be used in automation. Tests without GitHub: `python3 .github/forge/tests/run.py`.

**Exit:** labeling an issue `forge` in the target ends in a reviewed PR there, on Claude Code. Met on the sandbox.

## Phase 2 — Per-target configuration

- `.forge/config.yml` support, with "can only narrow" enforced and tested.
- Onboarding guide for a new target.

**Exit:** a second, different repository is onboarded without touching engine code.

## Not planned

Dropped by [ADR-0025](decisions/0025-scope-claude-only.md): runners for other providers (Codex, Copilot), provider and model
selection per request, a service on Azure with durable state, other request sources (Jira, Azure DevOps) and MCP servers
for the agents. Quotas, usage visibility and a secret rotation guide remain useful, with no date.
