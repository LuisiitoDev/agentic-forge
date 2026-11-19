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

## Status and labels

Agents report only a `status` in their result. They never name labels. The workflow maps the status to the
next step and applies the labels, restricted to its allowlist.

| Agent | `status` | Workflow action |
|---|---|---|
| `ba` | `complete` | Label `planning` |
| `ba` | `needs-clarification` | Label `needs-clarification`; resumes when the requester replies |
| `planner` | `ready-for-review` | Commit the plan, open the draft PR, label `spec-review` |
| `planner` | `needs-clarification` | Label `needs-clarification` |
| `coder`, `infra` | `ready-for-review` | Counts toward the gate; the Reviewer runs when every agent that ran is ready |
| `reviewer` | `pass` | Label `needs-review` |
| `reviewer` | `changes-requested` | Start a replan; the revised plan needs human approval again |
| any | `blocked` | Label `blocked` and hand the issue to a human |

If an agent's `build`, `tests` or `validation` is not `passed`, its status must be `blocked`. The workflow
rejects a result that breaks this rule.

## Plan files

The Planner writes `spec.md`, `plan.md` and `tasks.md` to `.forge/requests/<issue-number>/` in the target.
Each task has an owner (`app` or `infra`) and the paths it changes. The Planner's result lists every changed
path; the approver is checked against `CODEOWNERS` for them ([ADR-0008](decisions/0008-approval-authority.md)).

## Defaults

The `limits` in each spec are starting values and may be tuned. Coder and Infra ship with empty `write_paths`
on purpose: without a target configuration they are read-only, which is the safe default.

## Local use

If a developer wants to use an agent interactively in a provider's own tool, a small script may generate the
provider-specific file from the neutral spec. Generated files are never edited by hand and are not the source
of truth.
