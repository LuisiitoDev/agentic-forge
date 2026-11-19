# ADR-0003: Provider-neutral agent spec

**Status:** Accepted

## Context

Agents were defined in a Claude Code subagent format: a model setting and a list of Claude tool names. That
format cannot be reused by other providers.

## Decision

Define agents in `.github/forge/agents/*.md` with neutral frontmatter: `name`, `description`, `capabilities`
(`read`, `search`, `edit`, `shell`), `limits` and `write_paths`. They live inside `.github/` because `gh aw` can
only import files from there ([ADR-0009](0009-github-agentic-workflows.md)). No model and no provider tool names. See
[agent spec](../agent-spec.md).

Each runner translates capabilities to its provider's permission model. Provider-specific files, when needed
for local use, are generated from the spec and never edited by hand.

## Consequences

- One prompt body serves every provider.
- The model is chosen by repository configuration, not by the spec.
- Capabilities are coarse. Finer provider-specific control is not expressible and is intentionally excluded.
