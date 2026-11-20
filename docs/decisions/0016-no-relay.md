# ADR-0016: No relay and no hosted service for intake

**Status:** Accepted. Supersedes [ADR-0007](0007-webhook-relay.md) completely and withdraws the relay evolution
and the relay alternative in [ADR-0015](0015-reusable-workflow-intake.md).

## Context

[ADR-0007](0007-webhook-relay.md) put a stateless relay (an Azure Function) between GitHub App webhooks and the
engine. [ADR-0015](0015-reusable-workflow-intake.md) replaced it with a reusable workflow that each target calls,
but kept the relay as a "later evolution" and as code in the repository. A relay implementation with unit tests
was merged for that reason.

Keeping it has a cost: code, tests, a deployment guide and a second dispatch path to maintain and secure, for a
case (many targets, or caller files that become a burden) that has not happened.

## Decision

1. **The only intake is the reusable workflow** of ADR-0015. There is no relay, no webhook, and no service to
   host for intake.
2. **The relay code, its tests and its settings are removed.** They remain in the history of `main`, and may be
   read for reference, never restored without being asked.
3. **The GitHub App has no webhook.** It only supplies short-lived, repository-scoped tokens. Its manifest is
   [.github/forge/app-manifest.json](../../.github/forge/app-manifest.json).
4. **The dispatch payload stays the stable contract** (documented in the [intake spec](../intake-spec.md) and
   read by [forge-orchestrator.yml](../../.github/workflows/forge-orchestrator.yml)). If a different producer is
   ever needed, it is a new ADR that must emit that payload. [ADR-0004](0004-runtime-as-future-engine.md) is the
   place a service would come back.

## Consequences

- Less to build, host and secure: no public endpoint, no Azure Function, no webhook secret.
- Webhook redelivery no longer applies to intake. Re-running a failed intake run from the Actions UI is the
  equivalent, and stages stay idempotent.
- Intake cost stays tied to a caller file and a dispatch credential in every target (the cost ADR-0005 wanted to
  avoid, accepted in ADR-0015).
- Text in older ADRs that mentions the relay (ADR-0001, 0005, 0010, 0012) describes the decision at the time. Read
  "the relay" there as "the intake workflow". Pull request approval events (ADR-0010, ADR-0012), left for later,
  would need an extra trigger in the caller workflow.
