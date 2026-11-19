# Agent spec

An agent is a Markdown file with a small provider-neutral frontmatter and a prompt body. Specs live in
`agents/`. Nothing in them names a provider, a model or a provider's tool names.

## Format

```markdown
---
name: coder
description: Implements the approved plan on the plan branch
capabilities: [read, search, edit, shell]
limits:
  max_turns: 40
  timeout_minutes: 45
write_paths: [frontend/, backend/]
---

# Coder
<role, inputs, procedure, output format>
```

## Fields

| Field | Required | Meaning |
|---|---|---|
| `name` | yes | Unique id, matches the caller's `agent` input |
| `description` | yes | One line; what the agent is for |
| `capabilities` | yes | Subset of `read`, `search`, `edit`, `shell` |
| `limits.max_turns` | no | Turn budget where the provider supports it |
| `limits.timeout_minutes` | no | Hard timeout |
| `write_paths` | no | Paths the agent may commit. Empty means read-only |

`model` is intentionally absent. The model is chosen by repository configuration (a variable), never by the
spec, so one spec serves every provider.

## Body

The body is the prompt. It must state, for each agent:

1. **Role and boundaries**: what it does and what it never does.
2. **Inputs**: which result comments and data it reads, treated as untrusted.
3. **Procedure**: ordered steps.
4. **Output**: the exact result comment format, including the marker and YAML block (see
   [result protocol](../README.md#result-protocol)).

## Rules

- The body must not instruct the agent to call GitHub, add labels, open or merge PRs, or approve anything.
  The workflow does these after reading the result.
- The body must not assume a provider's tools. Describe actions ("search the code"), not tool names.
- Capabilities are a request. A runner that cannot enforce them refuses the run.

## Agents

| Agent | Capabilities | Writes |
|---|---|---|
| `ba` | `read`, `search` | nothing |
| `planner` | `read`, `search`, `edit`, `shell` | specification and plan directories |
| `coder` | `read`, `search`, `edit`, `shell` | application source |
| `infra` | `read`, `search`, `edit`, `shell` | infrastructure source |
| `reviewer` | `read`, `search`, `shell` | nothing |

Writable paths are not fixed in the spec for a central engine. They come from the target's configuration
(`paths.app`, `paths.infra` in [target repositories](target-repositories.md)), intersected with what the
engine allows. A spec's `write_paths` is only a default for targets that provide no configuration.

## Local use

If a developer wants to use an agent interactively in a provider's own tool, a small script may generate the
provider-specific file from the neutral spec. Generated files are never edited by hand and are not the source
of truth.
