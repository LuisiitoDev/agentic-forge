# ADR-0005: Requests enter through GitHub App webhooks

**Status:** Accepted. Supersedes the earlier CLI-based intake.

## Context

Requests must come from any of the owner's repositories without installing pipeline code in each one. We
considered a CLI, a workflow in each target, and a GitHub App.

## Decision

A **GitHub App** installed on each target delivers webhooks. Adding the label `forge` to an issue in a target
is the request. The issue's title and body are the request text.

## Alternatives considered

- **A CLI.** Works outside GitHub, but needs a client to build and authenticate, and puts the request away
  from where the work happens. Rejected for now.
- **A workflow in every target.** Simple, but must be copied to and maintained in every repo.

## Consequences

- The requester works only in the target repository: issue, comments, labels and the PR are all there.
- Webhooks need an HTTP endpoint, which Actions cannot be. See [ADR-0007](0007-webhook-relay.md).
- Authority comes from GitHub: only a human with write access to the target can start or approve a request.
- Labels written by the engine are themselves events and must not cause loops (see
  [security and governance](../security-and-governance.md)).
