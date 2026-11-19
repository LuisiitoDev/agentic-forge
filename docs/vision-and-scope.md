# Vision and scope

## Problem

Teams that automate development with AI agents usually end up tied to one model vendor, and to one repository.
Agent definitions, tool permissions, secrets and CLI calls use a vendor's vocabulary, and the automation is
copied into every repo. Switching model, or serving a second repo, means rewriting or duplicating the pipeline.

## Vision

A **central engine**. In any of your repositories you open an issue describing what to build and label it
`forge`. The engine analyses, plans, asks for human approval, implements, reviews, and delivers a **pull
request in that same repository**. The model that does the work is a replaceable part: the same request runs on Claude,
Codex/ChatGPT or GitHub Copilot by changing one setting.

## Principles

1. **One engine, many repositories.** The engine is installed once. Target repos need no pipeline code.
2. **Events drive everything.** A request is an explicit event: a human labels an issue. Nothing polls.
3. **Intent is separate from execution.** The workflow says what happens and in which order. A runner decides
   how a given model does it.
4. **Humans approve consequences.** Agents plan, build and review. A human approves the plan and merges.
5. **Agents are sandboxed by design.** No GitHub access, a fixed set of capabilities, a fixed set of writable
   paths. Request and repository content is data, never instructions.
6. **Fail closed.** If a runner cannot enforce the requested restrictions, it refuses to run.
7. **Least privilege per target.** Access to a target repo is scoped to that repo and expires.
8. **Provider-neutral core.** No provider vocabulary outside a runner.

## How it is used

```
issue + label `forge` in a target ──► GitHub App ──► relay ──► engine repo (Actions) ──► agents ──► PR in the target
                                                                    ▲                                  │
                                                                    └──── human approval gates ◄───────┘
```

See [request contract](request-contract.md) and the [architecture](../README.md#architecture).

## In scope

- Request intake through a GitHub App and webhooks, for any allowed repository.
- A thin, stateless relay between webhooks and the engine.
- Pipeline: analysis, planning, approval, implementation, review, replan.
- Runners for Claude, Codex (ChatGPT) and GitHub Copilot.
- A provider-neutral agent specification and per-target configuration.
- Documentation sufficient to reproduce and extend the above.

## Out of scope (for now)

- A hosted control plane, web UI, multi-tenancy for third parties, or billing.
- Repositories outside an explicit allowlist you control.
- Automatic model selection or unbounded autonomous loops.
- A full engine service with durable state (see [ADR-0004](decisions/0004-runtime-as-future-engine.md)). The
  only service we run at first is the stateless relay.

## Audience

The maintainer who owns the engine and the people who submit requests, plus the agents that run inside it.

## Open questions

- **Team owners in `CODEOWNERS`.** Expanding a team may need an extra App permission
  ([ADR-0008](decisions/0008-approval-authority.md)). To verify.
- **Prior art.** A .NET runtime (orchestrator, governance, SQLite persistence) exists in the history of
  `main`. It was removed from this branch. Its design informs the [runner contract](runner-contract.md).
