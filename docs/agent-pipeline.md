# Agent pipeline and trigger labels

## How the agents run

Each agent is a Claude Code subagent defined in `.claude/agents/` (`ba.md`, `planner.md`, `coder.md`, `infra.md`, `reviewer.md`). The wrapper workflows (`ba.yml`, `planner.yml`, `coder.yml`, `infra-agent.yml`, `reviewer.yml`) call the shared runner `agent-run.yml`, which:

1. Fetches the issue (title, body, labels, comments) and the plan PR, as JSON data.
2. Runs `claude -p --agent <name>` with a per-agent tool allowlist. The agent has no GitHub access.
3. Posts the agent's result as a comment, starting with its `<!-- <agent>:result -->` marker. The orchestrator reads these comments.
4. Applies the labels and status in the result, but only from an allowlist (`.github/scripts/apply-result.py`). An agent cannot add `ready-for-dev`.
5. For the Planner, Coder and Infra agents, commits the allowed paths to `planning/issue-<n>` and pushes. The Planner also opens the draft PR.

## Starting from an issue

1. Open an issue with the feature or bug form in `.github/ISSUE_TEMPLATE/`, or add the **analysis** label to an existing issue.
2. The BA (`ba.yml`) asks questions when the request is unclear. It uses **needs-clarification** while it waits. A reply from a writer resumes it.
3. When the requirements are complete, BA removes **analysis** and adds **planning**. That label starts the Planner. BA does not call the Planner directly.
4. The Planner creates a draft PR marked **planning-output** and moves the issue to **spec-review**. A human reviews the plan and adds **ready-for-dev** to approve implementation.
5. The Agent Orchestrator runs Coder and, when needed, Infra, then Reviewer. Reviewer findings return to the Planner; a revised plan requires human approval again. A passing review leaves **needs-review** for the human merge decision.

## Label reference

| Label | Applied to | Purpose / trigger |
|---|---|---|
| `analysis` | Issue | Starts issue BA when added; included by the issue forms |
| `planning` | Issue | Starts Planner when added |
| `spec-review` | Issue | Plan awaits human review; does not start implementation |
| `ready-for-dev` | Issue | Human approval; starts Agent Orchestrator, which calls Coder, optional Infra, and Reviewer |
| `needs-clarification` | Issue | An agent needs human input; BA questions resume on a reply, Planner questions require re-adding `planning`, implementation blockers require re-adding `ready-for-dev` after resolution |
| `needs-review` | Issue | Reviewer passed; human decides whether to merge |
| `planning-output` | Pull request | Identifies the Planner's delivery PR; not an issue trigger |
| `bug` | Issue | Classification used with `analysis` or `planning`; not a standalone trigger |

Coder, Infra, and Reviewer do not have separate label triggers. The orchestrator owns their order. Labels such as `agent:planning`, `agent:implementing`, and `po:idea` are not triggers for these workflows.

## Secrets and tokens

Set two Actions secrets:

- **`CLAUDE_CODE_OAUTH_TOKEN`**: generate with `claude setup-token` and store the printed value.
- **`BA_ISSUE_TOKEN`**: a user PAT with **Issues: read and write** on this repository, owned by a user with write access (the trigger check needs that role). The runner uses it to apply labels. A label added with the default `GITHUB_TOKEN` does not start another workflow, so this token is what makes the `planning` handoff work. Do not put its value in a workflow or issue.

Result comments use the default `GITHUB_TOKEN` and are authored by `github-actions[bot]`. The orchestrator ignores comments from anyone else.

The Planner opens its PR with `GITHUB_TOKEN`. That requires **Settings → Actions → General → Allow GitHub Actions to create and approve pull requests**.

The repository needs every label in the table above. `gh label create` can add the missing ones.

## Limits

- The agents can read the checkout and run the tools in their allowlist (see each wrapper's `allowed_tools`). They cannot call GitHub, merge, or approve.
- Commits are limited to each agent's `commit_paths`, so the Planner cannot change product code and the Coder cannot change `.specify/` or `infra/`.
- The runner doesn't check the result for content beyond its labels. The orchestrator still decides what happens next.

## Validation

- Open an issue with the **analysis** label. Verify BA posts a `ba:result` comment and, when it has all the answers, starts the Planner through **planning**.
- Confirm **spec-review** does not start Coder. Add **ready-for-dev** only after approving the plan, then confirm the orchestrator runs the required agents.
- Resolve Planner clarification and re-add **planning**; verify stale clarification and review labels are removed as the state changes.

These checks create real issues and agent runs. Validating the YAML does not prove the agents behave correctly.

## GitHub documentation

- [Triggering a workflow from a workflow](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow#triggering-a-workflow-from-a-workflow): why `BA_ISSUE_TOKEN` is needed for label handoffs.
- [Reusable workflows](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows): how the wrappers call `agent-run.yml`.
- [Claude Code subagents](https://docs.claude.com/en/docs/claude-code/sub-agents): the `.claude/agents/` format.
