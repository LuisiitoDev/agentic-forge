---
name: forge-planner
description: Forge Planner agent. Turns complete requirements into a spec, a plan and tasks on a plan branch.
run-name: "forge-planner ${{ inputs.target }}#${{ inputs.issue }}"
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
      pr:
        description: Plan pull request number in the target (empty on the first planning pass)
        required: false
        type: string
      ref:
        description: Plan branch to check out (empty means the default branch)
        required: false
        type: string
permissions:
  contents: read
  issues: read
  pull-requests: read
concurrency:
  job-discriminator: ${{ inputs.target }}#${{ inputs.issue }}
engine:
  id: copilot
  model: gpt-4.1
max-turns: 40
timeout-minutes: 45
network: defaults
checkout:
  - fetch-depth: 0
  - repository: ${{ inputs.target }}
    path: target
    current: true
    ref: ${{ inputs.ref }}
    github-app:
      client-id: ${{ vars.FORGE_APP_ID }}
      private-key: ${{ secrets.FORGE_APP_KEY }}
      repositories: ["${{ inputs.target_repo_name }}"]
tools:
  edit:
  bash: true
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
  create-pull-request:
    target-repo: ${{ inputs.target }}
    title-prefix: "[forge] "
    draft: true
    preserve-branch-name: true
    allowed-files:
      - ".forge/requests/**"
    max: 1
  push-to-pull-request-branch:
    check-branch-protection: false
    target: ${{ inputs.pr }}
    target-repo: ${{ inputs.target }}
    required-title-prefix: "[forge] "
    max: 1
---

{{#runtime-import .github/forge/runtime/context.md}}

## Your delivery

Request: issue `${{ inputs.issue }}` in `${{ inputs.target }}`.

Make your changes in `target/` and deliver through safe outputs:

1. **First planning pass** (input `pr` is empty): write the plan files under `.forge/requests/${{ inputs.issue }}/`,
   commit them on a branch named exactly `forge/issue-${{ inputs.issue }}`, and call `create_pull_request`
   (title: the request title; the PR is a draft). The PR body must contain `Closes #${{ inputs.issue }}` only when
   the issue is in the same repository, which it is.
2. **Replan** (input `pr` is set to `${{ inputs.pr }}`): you are on the existing plan branch. Update the plan
   files and call `push_to_pull_request_branch`. Do not create a second PR.
3. Then call `add_comment` **exactly once**, on thread `${{ inputs.thread }}` (the request issue on the first pass,
   the plan pull request on a replan), with the complete result message defined in your specification
   (the `## Planner result: <status>` heading as the first line, the YAML block and the body; no HTML comments, they are stripped).

You may only change files under `.forge/requests/${{ inputs.issue }}/`.

{{#runtime-import .github/forge/agents/planner.md}}
