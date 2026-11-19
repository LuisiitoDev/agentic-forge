# ADR-0002: Model-agnostic through interchangeable runners

**Status:** Accepted

## Context

The previous pipeline called Claude Code directly in one workflow step, with Claude-style tool rules and a
Claude-only secret. We need Claude, Codex (ChatGPT) and GitHub Copilot, selectable by configuration.

## Decision

Isolate the model call behind a **runner** with a fixed [contract](../runner-contract.md). The shared
workflow selects a runner by input. All other pipeline steps remain unchanged.

We wrap each provider's agentic CLI rather than building our own tool loop.

## Alternatives considered

- **Own agent harness over raw model APIs.** Identical behaviour across models, but a large build and a new
  product to maintain. Deferred; it is the natural content of ADR-0004.
- **Provider-specific workflows.** Duplicates the orchestration for each provider.

## Consequences

- Adding a provider means adding one runner and filling its capability mapping.
- Behaviour may differ slightly between providers because each CLI has its own agent loop.
- Permission models differ, so runners must fail closed when a restriction cannot be enforced.
- Codex and Copilot capabilities are unverified and must be confirmed before their runners are enabled.
