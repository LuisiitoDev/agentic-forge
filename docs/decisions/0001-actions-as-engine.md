# ADR-0001: A central repository on GitHub Actions is the engine

**Status:** Accepted (revised: the engine is central and serves many repositories; intake revised by ADR-0015 and ADR-0016)

## Context

The engine must accept requests for many repositories, run agents in an isolated environment with a checkout
and developer tools, and keep humans in the loop. We considered Actions in a central repository, or our own
service.

## Decision

The engine is a dedicated repository whose GitHub Actions workflows run the pipeline. A request is a labeled
issue in a target repository, delivered by a reusable intake workflow that the target calls, as a `repository_dispatch`
([ADR-0015](0015-reusable-workflow-intake.md), [ADR-0016](0016-no-relay.md); the original design used a GitHub App
and a stateless relay, [ADR-0005](0005-request-intake.md), [ADR-0007](0007-webhook-relay.md)). Work is delivered to the target
repository ([ADR-0006](0006-target-access-and-config.md)).

## Consequences

- No engine hosting, queue or database to run at the start. There is no service to host for intake ([ADR-0016](0016-no-relay.md)).
- A runner VM per job gives checkout, tools and isolation for free.
- Request state is spread across labels and comments, and recovery is manual.
- Actions has usage limits and quotas that apply to the engine repo.
- Long or stateful workflows are awkward. This is the limitation [ADR-0004](0004-runtime-as-future-engine.md)
  proposes to address later.
