# Agentic Forge — Documentation

Agentic Forge is a **central, model-agnostic engine**. In any of your repositories you open an issue and label
it `forge`. The engine analyses, plans, asks for human approval, implements and reviews, then delivers a pull
request in that repository. The model doing the work is replaceable: Claude, Codex/ChatGPT or
GitHub Copilot.

This branch is a fresh start. The documentation comes first; no code exists yet.

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

The diagrams and the architecture (components, roles, labels, runner workflow, result protocol) are in the
[root README](../README.md).

## Status

| Item | Status |
|---|---|
| Central repo on GitHub Actions as engine | Accepted ([ADR-0001](decisions/0001-actions-as-engine.md)) |
| Model-agnostic runners: Claude, Codex, Copilot | Accepted ([ADR-0002](decisions/0002-model-agnostic-runners.md)) |
| Provider-neutral agent spec | Accepted ([ADR-0003](decisions/0003-neutral-agent-spec.md)) |
| .NET runtime on Azure as a future engine | Proposed ([ADR-0004](decisions/0004-runtime-as-future-engine.md)) |
| Requests enter through GitHub App webhooks | Accepted ([ADR-0005](decisions/0005-request-intake.md)) |
| Target access via GitHub App, untrusted config | Proposed ([ADR-0006](decisions/0006-target-access-and-config.md)) |
| Stateless relay: webhook to dispatch | Accepted ([ADR-0007](decisions/0007-webhook-relay.md)) |
| Approval = write access, narrowed by CODEOWNERS | Accepted ([ADR-0008](decisions/0008-approval-authority.md)) |

## Conventions

- Language: English. Format: Markdown, versioned in this repository.
- Statements about third-party tools (CLI flags, permission modes, API limits) are marked **To verify** until
  checked against the tool itself. Do not rely on them before then.
