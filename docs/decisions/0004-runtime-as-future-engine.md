# ADR-0004: A runtime on Azure as a possible future engine

**Status:** Proposed. Not started; depends on Phases 1 to 4 of the [roadmap](../roadmap.md). The engine
runs on Actions today ([ADR-0001](0001-actions-as-engine.md)); this is the planned evolution when Actions'
limits start to cost something.

## Context

Actions keeps orchestration state in labels and comments, recovers manually and cannot easily express longer
or stateful flows. A .NET runtime existed on `main` with a deterministic orchestrator, human approval gates,
checkpointing and recovery, behind provider-neutral ports.

## Proposal

Run an orchestrator as a service on **Azure Container Apps**:

- A host receives GitHub webhooks, answers quickly and executes runs in the background.
- Durable state in a network store (SQLite is not suitable on App Service or Container Apps).
- Agent steps that need a shell and a checkout run as isolated **Container Apps Jobs**, behind the same
  contract as a runner.
- A single replica at first, because startup recovery assumes it is the only owner of runs. Scaling out
  needs a per-run lease.

## Why the earlier phases prepare for it

The [runner contract](../runner-contract.md) mirrors the runtime's agent port. When the engine moves, the
implementation behind that contract changes. Agent specs, prompts and the human gates do not.

## Open points

- Whether a non-Actions engine is needed at all once Phases 1 to 3 are in use.
- The store technology and the lease mechanism.
- What programmatic surface each provider offers outside its CLI. Copilot is the least known.

## Decision criteria

Adopt only if there is evidence that manual recovery, state spread across comments or per-run isolation is
causing real cost.
