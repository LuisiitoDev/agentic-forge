# Agentic Forge vision

## What Agentic Forge solves

Agentic Forge provides a predictable way to define, run, observe, and recover
multi-step work that uses agents, language models, and tools. It separates
workflow intent from provider-specific implementations so a team can change a
model, agent implementation, tool, or execution environment without rewriting
the workflow.

The initial product is an **agentic runtime**: a small, embeddable core that
turns a workflow definition into a controlled `WorkflowRun`. It makes each
step's inputs, outputs, status, failure, approval, and artifacts explicit.
This gives developers a testable foundation instead of an opaque prompt loop.

## Intended users

- **Application developers** building agent-assisted features into an existing
  product.
- **Workflow authors** defining repeatable research, transformation, and
  automation processes.
- **Operators and reviewers** who need visibility, approval gates, and safe
  recovery when a run needs intervention.
- **Framework and adapter authors** integrating model, agent, tool, execution,
  and persistence providers.

Agentic Forge is not initially a hosted end-user agent marketplace. Its first
audience is the engineers and teams who build and operate agentic workflows.

## Maturity strategy

Agentic Forge evolves through separate maturity stages. A later stage is
earned by demonstrated usage; it is not a commitment to build a larger system
up front.

### Runtime

The Runtime executes a workflow in one process. It provides explicit run and
step state, provider-neutral ports, deterministic orchestration, approval
boundaries, observability hooks, and recovery from persisted state. The
Runtime is the MVP and must remain usable without a particular vendor,
deployment platform, or hosted control plane.

### Framework

The Framework grows around the Runtime when repeated application needs justify
it. It may add authoring APIs, reusable workflow and agent patterns, testing
utilities, adapter packages, configuration conventions, and local development
tools. Framework features must preserve the Runtime's contracts and remain
composable rather than hiding execution semantics.

### Platform

The Platform is an optional operational layer for multiple teams or
environments. It may provide a service API, scheduling, multi-tenant controls,
durable hosted storage, dashboards, quotas, and fleet operations. Platform
work begins only when real Runtime/Framework usage demonstrates a shared
operational need. The core must not depend on these platform capabilities.

## Product direction

Agentic Forge should make the safe path the easy path: define intent as a
workflow, execute through explicit adapters, pause at human decisions, record
what happened, and resume or compensate from a known state. Every capability
should be small enough to validate in isolation and useful without adopting a
broader platform.
