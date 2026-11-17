# Agentic Forge scope

This document defines the initial Runtime boundary and the constraints for
future Framework and Platform work. It is intentionally narrower than a
complete agent platform.

## MVP capabilities

The initial Runtime should support:

- declarative or programmatic workflows composed of ordered steps and explicit
  transitions;
- one `WorkflowRun` containing identifiable `StepRun` records;
- calling an agent, an LLM, or a tool through a provider-neutral adapter;
- inputs, outputs, errors, status, timing, and produced `Artifact` records;
- retries and resume from persisted run state, with clear limits and failure
  outcomes;
- human approval steps that pause a run and require an explicit decision;
- structured events and logs sufficient to observe a run without inspecting
  provider internals;
- validation at workflow and step boundaries; and
- in-process execution as the default deployment shape.

The MVP is a **modular monolith**, not a distributed platform. Modules have
clear ports and responsibilities, but they run together in one deployable
process. Splitting a module into a service requires measured operational or
scaling evidence and must not weaken determinism, recovery, or auditability.

## Explicit non-goals for the initial Runtime

The initial Runtime does not include:

- a hosted control plane, multi-tenancy, billing, quotas, or a marketplace;
- a required web UI, chat product, mobile client, or end-user agent builder;
- a required cloud, container orchestrator, queue, database, or observability
  vendor;
- automatic model selection, opaque autonomous planning, or unbounded agent
  loops;
- a universal memory, vector database, knowledge graph, or retrieval product;
- distributed scheduling, cross-region execution, or fleet management;
- a promise of exactly-once effects for arbitrary external tools; or
- provider-specific prompt, tool, identity, or data contracts in the core.

These may be Framework or Platform candidates later, but each requires a
separate usage-backed decision.

## Architectural principles and constraints

1. **Provider-agnostic.** Core workflow behavior depends on stable ports and
   normalized contracts, never on a named vendor or SDK.
2. **Workflow-driven.** Work is represented as explicit steps and transitions;
   hidden control flow in prompts is not the orchestration contract.
3. **Deterministic orchestration.** Given the same workflow, recorded inputs,
   decisions, and adapter results, the Runtime makes the same scheduling and
   state-transition choices. Nondeterministic provider output is recorded as
   data, not treated as control flow that cannot be replayed.
4. **Human-governed.** Workflows can require approval before consequential
   steps. Approval identity, decision, time, and reason are part of the run
   record.
5. **Observable.** Runs and steps expose structured lifecycle events, status,
   timing, errors, and correlation identifiers without requiring vendor logs.
6. **Recoverable.** State is durable at defined boundaries; transient failures
   can be retried, and interrupted runs can be resumed or explicitly failed.
   External effects must use idempotency or compensation where the adapter can
   provide it.
7. **Secure by default.** Credentials stay in provider or host configuration,
   least privilege is the default, sensitive data is not emitted
   accidentally, and tool execution requires explicit capability boundaries.
8. **Small and modular.** Domain contracts stay independent of transport,
   persistence, and provider SDKs. A module or service is added only when its
   boundary and operational cost are justified.

## What “agnostic” means

Agnostic does not mean that every provider has identical features. It means
the core owns portable contracts and makes provider-specific capability gaps
explicit.

| Boundary | Core contract | Adapter responsibility |
| --- | --- | --- |
| **LLM provider** | Request/response, model identity, usage, finish/error status, and optional structured output | Translate prompts, messages, streaming, token accounting, safety settings, and provider errors |
| **Agent implementation** | An agent can receive a typed task context and return a result, events, or a failure | Implement planning, prompting, state, and agent-specific behavior |
| **Tool provider** | A named tool has a schema, invocation input, result/error, and capability metadata | Call the external system, authenticate, enforce tool policy, and describe side effects/idempotency |
| **Execution provider** | A step can be started, monitored, cancelled where possible, and reported with a stable result | Run work locally or remotely and map platform lifecycle/failure semantics |
| **Persistence provider** | Run, step, approval, event, and artifact state can be saved and loaded by stable identifiers | Map the contract to a database, file store, or other durable store |
| **Platform integration** | Optional host hooks for configuration, secrets, scheduling, telemetry, and deployment | Integrate a specific cloud, CI system, queue, or observability product without making it a core dependency |

An LLM provider is therefore not an agent implementation; a tool provider is
not an execution provider; and an execution provider is not persistence.
Implementations may be supplied by one product, but the Runtime keeps those
roles conceptually and contractually separate.

## Glossary

- **Agent:** An implementation that uses a task context and available
  capabilities to produce a result or failure for a workflow step.
- **Workflow:** A versioned definition of steps, transitions, inputs, policies,
  and approval boundaries.
- **WorkflowRun:** One execution of one workflow version, including its
  lifecycle, decisions, outputs, and artifacts.
- **StepRun:** The execution record for one workflow step within a
  `WorkflowRun`, including attempts and provider results.
- **Provider:** A system that supplies a capability, such as an LLM, tool,
  execution environment, persistence store, or platform service.
- **Adapter:** The boundary implementation that translates a Provider's
  interface and semantics to an Agentic Forge port.
- **Tool:** A named, schema-described capability that an agent or step may
  invoke, potentially causing an external side effect.
- **Artifact:** A durable, addressable output or input associated with a run or
  step, such as text, structured data, a file, or a reference.
- **Approval:** An explicit human decision that permits, rejects, or requests
  revision of a workflow transition or consequential step.

## Boundary for subsequent work

New issues should identify which Runtime contract they change and whether the
capability belongs in the Runtime, Framework, or Platform stage. They must not
silently introduce a vendor requirement, distributed deployment assumption, or
unbounded autonomous behavior into the core.
