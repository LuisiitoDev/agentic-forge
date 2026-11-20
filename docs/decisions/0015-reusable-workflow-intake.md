# ADR-0015: Intake through a reusable workflow that targets call

**Status:** Accepted. The decision is accepted; the intake workflow and the caller template are not built yet.
It supersedes the intake part of [ADR-0005](0005-request-intake.md) (a workflow in every target was rejected
there) and makes the relay of [ADR-0007](0007-webhook-relay.md) optional: both are marked superseded. The open
points below must be verified before the first target is onboarded.

## Context

GitHub Actions cannot receive webhooks, so ADR-0007 puts a stateless relay (code, a host, a dispatch token) in
front of the engine. That is a public endpoint to deploy and secure before the first request can run. A
reusable workflow (`on: workflow_call`) removes the host: each target carries a short caller workflow that
runs on the issue event inside GitHub and hands it to the engine.

ADR-0005 rejected "a workflow in every target" because it must be copied to and maintained in every repo.
That cost is real; this ADR accepts a much smaller version of it in exchange for no hosting and real-time
events.

## Decision (proposed)

1. **The target adds one thin caller workflow**, triggered by `issues: [labeled]` and
   `issue_comment: [created]`. It contains no logic: it calls the engine's intake workflow and passes the
   event. It is pinned to a tag or SHA of the engine repository, not `@main`, so engine changes reach targets
   only when a release is cut.
2. **The engine owns an intake reusable workflow** (`workflow_call`). It does what the relay does today:
   - ignores events from bots (loop prevention), and events other than a `forge` or `ready-for-dev` label, or
     a comment on a `forge` issue (not a PR);
   - validates sender, target and issue number;
   - checks `github.repository` against the allowlist;
   - sends the same `repository_dispatch` (event type `forge`, same payload) to the engine repository.
3. **The orchestrator and the agents stay in the engine repository, unchanged.** Intake only forwards. The
   App key, provider credentials and variables remain engine secrets. A target holds no engine secret except
   the credential in point 4. The orchestrator still validates every field and the allowlist again before it
   creates any token ([security](../security-and-governance.md)).
4. **Dispatch credential.** The caller needs a credential that can send `repository_dispatch` to the engine
   repository and nothing else, one per org or per target. The target's own `GITHUB_TOKEN` cannot do this.
   An org-level secret keeps it to one place.
5. **The caller file is untrusted.** Someone with write access to a target can edit it, so intake is not a
   security boundary on its own. The boundary stays where it is: the allowlist and sender authority are checked
   in the engine, and approval needs a human with write access ([ADR-0011](0011-approval-by-write-access.md)).
   A target that edits its caller to skip the allowlist gains nothing, because the orchestrator checks again.
6. **Onboarding** a target adds the caller file and the dispatch credential, then the target joins the
   allowlist ([target repositories](../target-repositories.md)). No GitHub App webhook is required for intake.
   The App is still used by the orchestrator to act on the target.

## Evolution to code

The payload and the orchestrator do not change, so intake can be swapped later without touching the pipeline:

- **Relay.** The relay in [relay/](../../relay/) already produces the same payload. If targets grow in number,
  or a caller file in each becomes a maintenance burden, point the App's webhook at it and drop the caller
  files. The relay code is kept for this reason.
- **Service.** [ADR-0004](0004-runtime-as-future-engine.md) replaces both with a service's own webhook handler.

Rule: the dispatch payload is the stable contract. Whatever produces it (caller workflow, relay, service) is
replaceable.

## Alternatives considered

- **Relay only (ADR-0007).** Central, nothing in targets, but needs hosting and a public endpoint.
- **Run the whole pipeline inside the caller (`secrets: inherit`).** A reusable workflow runs in the caller's
  context, so every target would hold the App key and provider credentials and pay the runner minutes. Rejected:
  it breaks "secrets only in the engine".
- **Scheduled polling in the engine.** No hosting and no files in targets, but a 5 minute or worse delay and
  rate-limit pressure as targets grow. Remains a fallback.

## Open points (To verify)

- That a private engine repository can be called by other repositories (access settings, plan limits), and
  whether the engine must be public or in the same organisation.
- That a caller's `issues` and `issue_comment` events provide the label, sender and `pull_request` markers the
  intake relies on, and how they behave for forks.
- The minimum permission of the dispatch credential, and whether a fine-grained token can be scoped to one
  repository with `repository_dispatch` only.
- Whether `github.repository` and the actor in a called workflow cannot be spoofed by the caller.
- Whether pinning by tag works well for many targets, and how a release reaches them (for example Dependabot).
- Cost and run history landing in each target's Actions tab.

## Consequences

- No host, no public endpoint, real-time intake. The relay becomes optional.
- A small file and a credential in every target, which is the maintenance cost ADR-0005 wanted to avoid.
- Engine releases need versioning, because targets pin a ref.
- The security model is unchanged: intake is convenience, the engine re-checks everything.
