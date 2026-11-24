# Agentic Forge — Documentation

Agentic Forge is a **central engine**. In any of your repositories you open an issue and label
it `forge`. The engine analyses, plans, asks for human approval, implements and reviews, then delivers a pull
request in that repository. The agents run on Claude Code, inside a firewall.

Phase 1 is built and ran end to end on a sandbox repository; see the [roadmap](roadmap.md). The state of every
request is visible on the [status page](https://luisiitodev.github.io/agentic-forge/) (how it works: root README,
[Status page](../README.md#status-page)).

## Reading order

| # | Document | Answers |
|---|---|---|
| 1 | [Vision and scope](vision-and-scope.md) | What is this, what is it not, who is it for? |
| 2 | [Request contract](request-contract.md) | What is a request and what is its lifecycle? |
| 3 | [Target repositories](target-repositories.md) | How does the engine access and adapt to a repo? |
| 4 | [Agent spec](agent-spec.md) | How is an agent defined, independent of provider? |
| 5 | [Security and governance](security-and-governance.md) | What can an agent never do? |
| 6 | [Roadmap](roadmap.md) | In what order is this built? |
| 7 | [Decisions (ADRs)](decisions/) | Why was each choice made? |
| 8 | [GitHub App and engine setup](github-app-setup.md) | How are the App, the engine and a target configured? |
| 9 | [First-run checklist](first-run-checklist.md) | How is the first real run done and recorded? |
| 10 | [First run from a browser](first-run-mobile.md) | What is the shortest path to a first run, without a terminal? |
| 11 | [Intake spec](intake-spec.md) | What exactly does the intake workflow accept and forward? |

The diagrams and the architecture (components, roles, labels, workflows, models, defenses, result protocol, status
page) are in the [root README](../README.md).

## Status

### Current decisions

What the engine does today. Read these first.

| Item | Status |
|---|---|
| Central repo on GitHub Actions as engine | Accepted ([ADR-0001](decisions/0001-actions-as-engine.md)) |
| Provider-neutral agent spec | Accepted ([ADR-0003](decisions/0003-neutral-agent-spec.md)) |
| Target access via GitHub App, untrusted config | Proposed ([ADR-0006](decisions/0006-target-access-and-config.md)) |
| Approval = human with write access | Accepted ([ADR-0011](decisions/0011-approval-by-write-access.md)). Supersedes ADR-0008 (CODEOWNERS) |
| One shared plan PR is the working surface | Accepted ([ADR-0010](decisions/0010-shared-plan-pr.md)) |
| Approval policy: no self-approval, sensitive paths | Accepted ([ADR-0012](decisions/0012-approval-policy.md)). Refines ADR-0011 |
| Intake through a reusable workflow called by targets | Accepted ([ADR-0015](decisions/0015-reusable-workflow-intake.md)). Supersedes the intake part of ADR-0005 and ADR-0007 |
| The Reviewer reads CI results and security alerts (read-only) | Accepted ([ADR-0019](decisions/0019-reviewer-ci-and-security-tools.md)) |
| Orchestrator awaits the agent runs it starts (no `workflow_run`) | Accepted ([ADR-0018](decisions/0018-orchestrator-awaits-agent-runs.md)). Supersedes the `workflow_run` trigger of ADR-0009 |
| Every agent runs on Claude Code inside AWF with the subscription token | Accepted ([ADR-0022](decisions/0022-all-agents-on-claude-in-awf.md)). The fallback to Copilot was removed by ADR-0024 |
| The engine has no Infra agent: infrastructure is done by a person; the flow is BA, Planner, Coder, Reviewer | Accepted ([ADR-0023](decisions/0023-no-infra-agent.md)). Amends the ADRs that name the Infra agent |
| No relay, no hosted service for intake | Accepted ([ADR-0016](decisions/0016-no-relay.md)). Withdraws the relay evolution of ADR-0015 |
| Claude Code inside AWF is the only engine; `gh aw` and the Copilot fallback are removed | Accepted ([ADR-0024](decisions/0024-claude-code-only.md)) |
| Multi-provider runners, the Azure runtime, MCP servers and other request sources are dropped | Accepted ([ADR-0025](decisions/0025-scope-claude-only.md)) |
| Deterministic defenses against prompt injection: clean input, scan output, narrow tools | Accepted ([ADR-0026](decisions/0026-prompt-injection-defenses.md)) |
| A deterministic model router per agent (Haiku, Sonnet, Opus), inspired by Copilot Fusion but without a model in the decision | Accepted ([ADR-0027](decisions/0027-model-router.md)) |
| The orchestrator hands off a long wait to a new run with a fresh token | Accepted ([ADR-0028](decisions/0028-orchestrator-hands-off-long-waits.md)). Amends ADR-0018 |

### History

Superseded, withdrawn or never adopted. Kept for the reasoning; each record says what replaced it.

| Item | Status |
|---|---|
| Model-agnostic runners: Claude, Codex, Copilot | Accepted, then dropped: Claude Code is the only engine ([ADR-0002](decisions/0002-model-agnostic-runners.md), [ADR-0025](decisions/0025-scope-claude-only.md)) |
| .NET runtime on Azure as a future engine | Withdrawn ([ADR-0004](decisions/0004-runtime-as-future-engine.md), [ADR-0025](decisions/0025-scope-claude-only.md)) |
| Requests enter through GitHub App webhooks | Superseded in part by ADR-0015 ([ADR-0005](decisions/0005-request-intake.md)) |
| Stateless relay: webhook to dispatch | Superseded by ADR-0015 and withdrawn by ADR-0016 ([ADR-0007](decisions/0007-webhook-relay.md)) |
| Agents run as `gh aw` workflows; orchestrator is plain Actions | Superseded: the agents are Claude Code workflows ([ADR-0009](decisions/0009-github-agentic-workflows.md), [ADR-0024](decisions/0024-claude-code-only.md)) |
| Read-only MCP servers for agents, write-back by the workflow (GitHub, Atlassian, Azure DevOps) | Withdrawn ([ADR-0013](decisions/0013-agent-mcp-servers.md), [ADR-0025](decisions/0025-scope-claude-only.md)) |
| Request sources beyond GitHub issues (Jira, Azure DevOps) | Withdrawn ([ADR-0014](decisions/0014-external-request-sources.md), [ADR-0025](decisions/0025-scope-claude-only.md)) |
| GitHub Copilot is the engine for the first real run | Superseded by ADR-0022 and removed by ADR-0024 ([ADR-0017](decisions/0017-copilot-engine-for-phase-1.md)) |
| The engine configuration chooses how the BA runs: Copilot or Claude Code, no automatic fallback | Superseded ([ADR-0020](decisions/0020-ba-engine-choice.md), [ADR-0024](decisions/0024-claude-code-only.md)) |
| The BA runs on Claude by default, with one retry on Copilot when it fails without a result | Superseded by ADR-0024: there is no fallback ([ADR-0021](decisions/0021-claude-default-copilot-fallback.md)) |

## Conventions

- Language: English. Format: Markdown, versioned in this repository.
- Statements about third-party tools (CLI flags, permission modes, API limits) are marked **To verify** until
  checked against the tool itself. Do not rely on them before then.
