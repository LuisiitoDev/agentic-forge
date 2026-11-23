---
name: forge-reviewer
description: Forge Reviewer agent. Verifies the implementation and returns a verdict. Never approves or merges.
run-name: "forge-reviewer ${{ inputs.target }}#${{ inputs.issue }}"
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
  actions: read
  security-events: read
  vulnerability-alerts: read
concurrency:
  job-discriminator: ${{ inputs.target }}#${{ inputs.issue }}
engine:
  id: copilot
  model: gpt-4.1
max-turns: 40
timeout-minutes: 45
network:
  allowed:
    - defaults
    - node
    - dotnet
    - python
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
  bash: true
  github:
    toolsets: [issues, pull_requests, repos, actions, code_security, dependabot, secret_protection]
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

You are read-only. The plan branch is checked out in `target/`
(input `ref`: `${{ inputs.ref }}`, plan pull request: `${{ inputs.pr }}`). Anything you change in the working
tree is discarded.

Deliver your result by calling `add_comment` **exactly once**, on the plan pull request (thread `${{ inputs.thread }}`), with the complete result message defined in your
specification (the `<!-- reviewer:result -->` marker, the heading, the YAML block and the body). You have no
other safe output.

{{#runtime-import .github/forge/agents/reviewer.md}}
