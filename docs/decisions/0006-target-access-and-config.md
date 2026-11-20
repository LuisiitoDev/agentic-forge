# ADR-0006: Target access through a GitHub App, with untrusted per-target config

**Status:** Proposed

## Context

The engine must read and write many repositories without holding broad, long-lived credentials, and agents
must adapt to each repository's conventions.

## Decision

1. Access uses the **GitHub App** installed on each target (the App only supplies tokens; it has no webhook,
   see [ADR-0016](0016-no-relay.md)). Each run mints a short-lived installation token scoped to one
   repository. The App needs only issues, contents and pull request access (**to verify** exact permissions).
2. The engine keeps an **allowlist** of targets.
3. Targets may provide `.forge/config.yml`. It is **untrusted data** that can only narrow limits.
4. The workflow, not the agent, clones, pushes and opens PRs.

See [target repositories](../target-repositories.md).

## Alternatives considered

- **A personal access token.** Simple, but broad, long-lived and tied to a person.
- **A deploy key per repo.** Narrow, but awkward to manage for PRs and API calls.

## Consequences

- Onboarding a target takes an App installation and an allowlist entry.
- A compromised run exposes at most one repository, for the token's lifetime.
- The App's own private key becomes the most sensitive secret in the engine.
