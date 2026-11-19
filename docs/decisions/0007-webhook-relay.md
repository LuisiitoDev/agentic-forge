# ADR-0007: A stateless relay turns webhooks into dispatches

**Status:** Accepted

## Context

GitHub App webhooks need a public HTTP endpoint. The engine runs on GitHub Actions
([ADR-0001](0001-actions-as-engine.md)), which cannot receive webhooks.

## Decision

Run a **thin, stateless relay** (for example an Azure Function or a small Container App). For each webhook it:

1. Verifies the signature (`X-Hub-Signature-256`) and rejects anything unsigned or invalid.
2. Ignores events it does not handle, and events caused by the App's own bot (loop prevention).
3. Checks the `target` against the allowlist.
4. Sends a `repository_dispatch` to the engine repository with a **minimal** payload: event type and action,
   target, issue number, sender and the delivery id.

The relay keeps no state, runs no agent and holds no model credentials. All decisions beyond routing are made
in the engine workflows, which re-read the issue with a fresh, scoped token.

## Dispatch payload

The relay sends `repository_dispatch` with event type `forge` and this `client_payload`. The orchestrator
validates every field before using it.

| Field | Value |
|---|---|
| `event` | `issues` or `issue_comment` |
| `action` | `labeled` or `created` |
| `target` | `owner/name` |
| `issue` | Issue number |
| `sender` | Login of the user who acted |
| `label` | Label name, when `action` is `labeled` |
| `delivery` | Webhook delivery id, for traceability |

## Alternatives considered

- **Run the whole engine as a service** ([ADR-0004](0004-runtime-as-future-engine.md)). Stronger state and
  recovery, but much more to build first. Still the planned evolution.

## Consequences

- One small public endpoint to secure and monitor.
- The dispatch payload is tiny, so payload size limits do not matter.
- The credential the relay uses to dispatch must be able to dispatch to the engine repo and nothing else
  (**to verify** the minimum permission).
- If the relay is down, events are lost unless GitHub's webhook redelivery is used. Because stages are
  idempotent, redelivery is safe.
- When ADR-0004 is adopted, the relay is replaced by the service's own webhook handler and nothing else in
  the pipeline changes.
