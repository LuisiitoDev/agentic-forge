# ADR-0001: A central repository on GitHub Actions is the engine

**Status:** Accepted (revised: the engine is central and serves many repositories)

## Context

The engine must accept requests for many repositories, run agents in an isolated environment with a checkout
and developer tools, and keep humans in the loop. We considered Actions in a central repository, or our own
service.

## Decision

The engine is a dedicated repository whose GitHub Actions workflows run the pipeline. A request is a labeled
issue in a target repository, delivered by a GitHub App and a stateless relay as a `repository_dispatch`
([ADR-0005](0005-request-intake.md), [ADR-0007](0007-webhook-relay.md)). Work is delivered to the target
repository ([ADR-0006](0006-target-access-and-config.md)).

## Consequences

- No engine hosting, queue or database to run at the start. The only service is the stateless relay.
- A runner VM per job gives checkout, tools and isolation for free.
- Request state is spread across labels and comments, and recovery is manual.
- Actions has usage limits and quotas that apply to the engine repo.
- Long or stateful workflows are awkward. This is the limitation [ADR-0004](0004-runtime-as-future-engine.md)
  proposes to address later.
