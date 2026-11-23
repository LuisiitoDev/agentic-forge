# Agentic Forge — Documentation

Agentic Forge is a **central, model-agnostic engine**. In any of your repositories you open an issue and label
it `forge`. The engine analyses, plans, asks for human approval, implements and reviews, then delivers a pull
request in that repository. The model doing the work is replaceable: Claude, Codex/ChatGPT or
GitHub Copilot.

Phase 1 is built and in testing: the agent workflows, the orchestrator and the intake exist and have tests, and
parts of the pipeline have run on real GitHub. It has not run end to end yet ([roadmap](roadmap.md)).

## Reading order

| # | Document | Answers |
|---|---|---|
| 1 | [Vision and scope](vision-and-scope.md) | What is this, what is it not, who is it for? |
| 2 | [Request contract](request-contract.md) | What is a request and what is its lifecycle? |
| 3 | [Target repositories](target-repositories.md) | How does the engine access and adapt to a repo? |
| 4 | [Runner contract](runner-contract.md) | What does a model adapter have to do? |
| 5 | [Agent spec](agent-spec.md) | How is an agent defined, independent of provider? |
| 6 | [Security and governance](security-and-governance.md) | What can an agent never do? |
| 7 | [Roadmap](roadmap.md) | In what order is this built? |
| 8 | [Decisions (ADRs)](decisions/) | Why was each choice made? |
| 9 | [GitHub App and engine setup](github-app-setup.md) | How are the App, the engine and a target configured? |
| 10 | [First-run checklist](first-run-checklist.md) | How is the first real run done and recorded? |
| 11 | [Handoff contract](handoff-contract.md) | What is left, and how is it continued? |

The diagrams and the architecture (components, roles, labels, runner workflow, result protocol) are in the
[root README](../README.md).

## Status

| Item | Status |
|---|---|
| Central repo on GitHub Actions as engine | Accepted ([ADR-0001](decisions/0001-actions-as-engine.md)) |
| Model-agnostic runners: Claude, Codex, Copilot | Accepted ([ADR-0002](decisions/0002-model-agnostic-runners.md)) |
| Provider-neutral agent spec | Accepted ([ADR-0003](decisions/0003-neutral-agent-spec.md)) |
| .NET runtime on Azure as a future engine | Proposed ([ADR-0004](decisions/0004-runtime-as-future-engine.md)) |
| Requests enter through GitHub App webhooks | Superseded in part by ADR-0015 ([ADR-0005](decisions/0005-request-intake.md)) |
| Target access via GitHub App, untrusted config | Proposed ([ADR-0006](decisions/0006-target-access-and-config.md)) |
| Stateless relay: webhook to dispatch | Superseded by ADR-0015 and withdrawn by ADR-0016 ([ADR-0007](decisions/0007-webhook-relay.md)) |
| Approval = human with write access | Accepted ([ADR-0011](decisions/0011-approval-by-write-access.md)). Supersedes ADR-0008 (CODEOWNERS) |
| Agents run as `gh aw` workflows; orchestrator is plain Actions | Accepted for Phase 1 ([ADR-0009](decisions/0009-github-agentic-workflows.md)) |
| One shared plan PR is the working surface | Accepted ([ADR-0010](decisions/0010-shared-plan-pr.md)) |
| Approval policy: no self-approval, sensitive paths | Accepted ([ADR-0012](decisions/0012-approval-policy.md)). Refines ADR-0011 |
| Read-only MCP servers for agents, write-back by the workflow (GitHub, Atlassian, Azure DevOps) | Accepted as a design; no server is declared until its credential exists, a placeholder stops the agent ([ADR-0013](decisions/0013-agent-mcp-servers.md)) |
| Request sources beyond GitHub issues (Jira, Azure DevOps) | Proposed ([ADR-0014](decisions/0014-external-request-sources.md)) |
| Intake through a reusable workflow called by targets | Accepted ([ADR-0015](decisions/0015-reusable-workflow-intake.md)). Supersedes the intake part of ADR-0005 and ADR-0007 |
| GitHub Copilot is the engine for the first real run | Superseded in part by ADR-0020 ([ADR-0017](decisions/0017-copilot-engine-for-phase-1.md)). Refines ADR-0009 |
| The Reviewer reads CI results and security alerts (read-only) | Accepted ([ADR-0019](decisions/0019-reviewer-ci-and-security-tools.md)) |
| Orchestrator awaits the agent runs it starts (no `workflow_run`) | Accepted ([ADR-0018](decisions/0018-orchestrator-awaits-agent-runs.md)). Supersedes the `workflow_run` trigger of ADR-0009 |
| No relay, no hosted service for intake | Accepted ([ADR-0016](decisions/0016-no-relay.md)). Withdraws the relay evolution of ADR-0015 |
| Claude is the engine (model set by `FORGE_MODEL`); rate limited agent runs are retried with backoff | Accepted for Phase 1 ([ADR-0020](decisions/0020-claude-engine-and-rate-limits.md)). Supersedes in part ADR-0017 |

## Conventions

- Language: English. Format: Markdown, versioned in this repository.
- Statements about third-party tools (CLI flags, permission modes, API limits) are marked **To verify** until
  checked against the tool itself. Do not rely on them before then.
