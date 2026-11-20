# Roadmap

Documentation first, then code. Each phase is shippable and verified before the next starts.

## Phase 0 — Documentation (current)

- Vision, request contract, architecture, runner contract, agent spec, target repositories, security, ADRs.
- Resolve the open questions in [vision and scope](vision-and-scope.md#open-questions).
- Verify the open **To verify** items: App permissions, dispatch permission, and (relay only) webhook redelivery behaviour.

**Exit:** the documents are reviewed and agreed.

## Phase 1 — Engine skeleton with one target

- GitHub App used only for short-lived tokens to act on targets (no webhook), a reusable intake workflow called
  by each target, and `repository_dispatch` into the engine repo ([ADR-0015](decisions/0015-reusable-workflow-intake.md)). The stateless relay
  ([ADR-0007](decisions/0007-webhook-relay.md)) is the later alternative.
- Allowlist, sender-authority checks, loop prevention and idempotent stages.
- The label-driven pipeline and the shared runner workflow, working against one target repository.
- Claude runner, with neutral agent specs for all five agents.

**Built, not yet run end to end:** the five agent workflows (Claude), their shared runtime context, the
orchestrator (`orchestrate.sh`) and the intake (`intake.sh`, [forge-intake.yml](../.github/workflows/forge-intake.yml)
and the [caller template](../.github/forge/templates/forge-caller.yml), see the [intake spec](intake-spec.md)),
all tested without GitHub (18, 46, 82 and 13 checks). **Built, not deployed:** the optional relay
([relay/](../relay/)) and the App manifest, with a [setup guide](relay-setup.md) and a
[first-run checklist](first-run-checklist.md). **Not created:** the GitHub App,
the dispatch token and the first target. **Not built:** per-provider selection. See the "Not yet verified" list in
[ADR-0009](decisions/0009-github-agentic-workflows.md) and the open points in the intake spec.

**Exit:** labeling an issue `forge` in the target ends in a reviewed PR there, on Claude.

## Phase 2 — Per-target configuration

- `.forge/config.yml` support, with "can only narrow" enforced and tested.
- Onboarding guide for a new target.

**Exit:** a second, different repository is onboarded without touching engine code.

## Phase 3 — Codex runner

- Verify non-interactive mode, permission model and result extraction.
- Fill the Codex column of the capability table; refuse agents whose capabilities cannot be enforced.

**Exit:** a request runs on Codex end to end, or the documented limits say which agents it can run.

## Phase 4 — Copilot runner

- Verify that a scriptable, non-interactive surface exists and what it can restrict.
- Same steps as Phase 3.

**Exit:** as Phase 3.

## Phase 5 — Operations

- Provider and model selection per request and by default.
- Quotas and usage visibility, troubleshooting guide, secret rotation guide.

## Later (not committed)

- Run the orchestrator as a service on Azure Container Apps with durable state
  ([ADR-0004](decisions/0004-runtime-as-future-engine.md)).
- Isolated per-run execution using Container Apps Jobs.
- Additional request sources beyond GitHub issues (for example a CLI or chat command, Jira or Azure DevOps;
  proposed in [ADR-0014](decisions/0014-external-request-sources.md)).
- Read-only MCP servers for agents (Atlassian, Azure DevOps), with write-back by the workflow
  (proposed in [ADR-0013](decisions/0013-agent-mcp-servers.md)).
