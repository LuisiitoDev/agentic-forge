---
name: forge-ba
description: Forge BA agent. Turns a request into testable requirements, or asks clarifying questions.
run-name: "forge-ba ${{ inputs.target }}#${{ inputs.issue }}"
on:
  workflow_dispatch:
    inputs:
      target:
        description: Target repository, owner/name
        required: true
        type: string
      issue:
        description: Request issue number in the target
        required: true
        type: string
      thread:
        description: Issue or pull request number where the agent posts its result
        required: true
        type: string
      target_repo_name:
        description: Name of the target repository without the owner. Scopes the App token to that one repository
        required: true
        type: string
permissions:
  contents: read
  issues: read
  pull-requests: read
# One agent run per request at a time, across all five agents (ADR-0020). The group must differ from the
# orchestrator's (`forge-request-...`): the orchestrator holds its group while it waits for this run.
concurrency:
  group: forge-agent-${{ inputs.target }}-${{ inputs.issue }}
  cancel-in-progress: false
  queue: max
  job-discriminator: ${{ inputs.target }}#${{ inputs.issue }}
# The engine is fixed at compile time (gh aw does not accept an expression for `engine.id`); the model is not:
# set the repository variable FORGE_MODEL to override the default (ADR-0020).
engine:
  id: claude
  model: ${{ vars.FORGE_MODEL || 'claude-sonnet-5-5' }}
max-turns: 20
timeout-minutes: 20
network: defaults
checkout:
  - fetch-depth: 1
  - repository: ${{ inputs.target }}
    path: target
    current: true
    github-app:
      client-id: ${{ vars.FORGE_APP_ID }}
      private-key: ${{ secrets.FORGE_APP_KEY }}
      repositories: ["${{ inputs.target_repo_name }}"]
tools:
  github:
    toolsets: [issues, pull_requests]
    # Without this the GitHub tools drop the engine bot's own comments (the BA result) for the next agent:
    # gh aw filters content below `approved` and an App bot is not `approved` by default.
    min-integrity: approved
    trusted-users: ["${{ vars.FORGE_BOT_LOGIN }}"]
    github-app:
      client-id: ${{ vars.FORGE_APP_ID }}
      private-key: ${{ secrets.FORGE_APP_KEY }}
      repositories: ["${{ inputs.target_repo_name }}"]
safe-outputs:
  # The orchestrator already reports a failed agent on the request issue, so gh aw does not open its own issue.
  report-failure-as-issue: false
  github-app:
    client-id: ${{ vars.FORGE_APP_ID }}
    private-key: ${{ secrets.FORGE_APP_KEY }}
    repositories: ["${{ inputs.target_repo_name }}"]
  add-comment:
    target: ${{ inputs.thread }}
    target-repo: ${{ inputs.target }}
    max: 1
---

{{#runtime-import .github/forge/runtime/context.md}}

## Your delivery

Request: issue `${{ inputs.issue }}` in `${{ inputs.target }}`.

The pull request does not exist yet, so you post on the request issue (thread `${{ inputs.thread }}`).

Deliver your result by calling the `add_comment` safe output **exactly once**. The comment body is the complete
result message defined in your specification (the `## BA result: <status>` heading as the first line, the YAML block and
the body; no HTML comments, they are stripped). You have no other safe output.

{{#runtime-import .github/forge/agents/ba.md}}
