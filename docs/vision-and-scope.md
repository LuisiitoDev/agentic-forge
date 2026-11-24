# Vision and scope

## Problem

Teams that automate development with AI agents usually end up tied to one model vendor, and to one repository.
Agent definitions, tool permissions, secrets and CLI calls use a vendor's vocabulary, and the automation is
copied into every repo. Switching model, or serving a second repo, means rewriting or duplicating the pipeline.

## Vision

A **central engine**. In any of your repositories you open an issue describing what to build and label it
`forge`. The engine analyses, plans, asks for human approval, implements, reviews, and delivers a **pull
request in that same repository**. The agents run on Claude Code. The agent specs are provider-neutral, so another engine
is possible, but none is planned ([ADR-0024](decisions/0024-claude-code-only.md)).

## Principles

1. **One engine, many repositories.** The engine is installed once. Target repos need no pipeline code.
2. **Events drive everything.** A request is an explicit event: a human labels an issue. Nothing polls.
3. **Intent is separate from execution.** The workflow says what happens and in which order. The workflow decides
   how a given model does it.
4. **Humans approve consequences.** Agents plan, build and review. A human approves the plan and merges.
5. **Agents are sandboxed by design.** No GitHub access, a fixed set of capabilities, a fixed set of writable
   paths. Request and repository content is data, never instructions.
6. **Fail closed.** If the delivery job cannot validate what an agent did, it publishes nothing.
7. **Least privilege per target.** Access to a target repo is scoped to that repo and expires.
8. **Provider-neutral core.** No provider vocabulary outside the Claude scripts and action.

## How it is used

```
issue + label `forge` in a target ──► caller workflow ──► intake ──► engine repo (Actions) ──► agents ──► PR in the target
                                                                    ▲                                  │
                                                                    └──── human approval gates ◄───────┘
```

See [request contract](request-contract.md) and the [architecture](../README.md#architecture).

## In scope

- Request intake through a thin reusable workflow that each allowed target calls ([ADR-0015](decisions/0015-reusable-workflow-intake.md)).
- Pipeline: analysis, planning, approval, implementation, review, replan.
- Agents on Claude Code, with a provider-neutral agent specification and per-target configuration.
- Documentation sufficient to reproduce and extend the above.

## Out of scope (for now)

- A hosted control plane, web UI, multi-tenancy for third parties, or billing.
- Repositories outside an explicit allowlist you control.
- Automatic model selection or unbounded autonomous loops.
- A full engine service with durable state (see [ADR-0004](decisions/0004-runtime-as-future-engine.md)). At first
  we run no service of our own.

## Audience

The maintainer who owns the engine and the people who submit requests, plus the agents that run inside it.

## Open questions

- **Prior art.** A .NET runtime (orchestrator, governance, SQLite persistence) exists in the history of
  `main`. It was removed from this branch. Its design is history only.
