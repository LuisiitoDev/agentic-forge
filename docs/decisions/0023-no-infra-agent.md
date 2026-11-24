# ADR-0023: The engine has no Infra agent

**Status:** Accepted. Built and tested without GitHub. Amends [ADR-0009](0009-github-agentic-workflows.md),
[ADR-0010](0010-shared-plan-pr.md), [ADR-0012](0012-approval-policy.md), [ADR-0013](0013-agent-mcp-servers.md),
[ADR-0017](0017-copilot-engine-for-phase-1.md) and [ADR-0022](0022-all-agents-on-claude-in-awf.md), which name it.

## Context

The flow had five agents: BA, Planner, Coder, Infra and Reviewer. The Infra agent implemented the plan's
infrastructure tasks (`owner: infra`), after the Coder and never in parallel, and wrote only the target's
`paths.infra`. The owner decided that infrastructure does not belong in the software development flow of this
engine: changing cloud resources, pipelines or infrastructure as code is work for a person, not for an unattended
agent, and the agent had never run on the current design.

## Decision

1. **The Infra agent is removed**: its spec, its `gh aw` workflow and lock, its Claude workflow, its entries in the
   Claude scripts (`configure_agent.py`, `validate_changes.py`, `deliver.py`, the prompt), the
   `FORGE_INFRA_ENGINE` and `FORGE_INFRA_FALLBACK` variables, and `paths.infra` of `.forge/config.yml`.
   The flow is BA, Planner, Coder, Reviewer.
2. **Infrastructure work is done by a person.** The Planner does not plan it: it lists what the request needs under
   `Out of scope (a person does it)` in its result and plans only the application work. Every task has
   `owner: app`; the result's `tasks` has only `app`.
3. **Fail closed.** If a plan still lists infrastructure tasks (`tasks.infra` above zero), the orchestrator refuses
   the approval with the reason and leaves the request in `spec-review`, so the plan can be redone without them. A
   plan with no application task is blocked ("The plan has no application tasks").
4. **The scope gate checks `paths.app` only.** After the Coder, the orchestrator goes straight to the scope gate and
   then the Reviewer. A target's `.forge/config.yml` with `paths.infra` is not an error, it is ignored.
5. Delivering as `infra` is refused by the Claude delivery script, and so is configuring it.

## Consequences

- One agent fewer to keep in step with its spec, its two workflows and its tests, and no agent writes infrastructure
  unattended.
- A request that needs infrastructure gets only its application half from the engine. The person who approves the
  plan reads the `Out of scope` section and does the rest.
- Older documents that describe the Infra agent (the ADRs above) are historical: they carry a note that points here.
