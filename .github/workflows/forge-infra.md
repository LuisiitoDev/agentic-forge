---
name: forge-infra
description: Forge Infra agent. Implements the approved infrastructure tasks on the plan branch.
run-name: "forge-infra ${{ inputs.target }}#${{ inputs.issue }}"
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
engine: claude
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
tools:
  edit:
  bash: true
  github:
    toolsets: [issues, pull_requests]
    github-app:
      client-id: ${{ vars.FORGE_APP_ID }}
      private-key: ${{ secrets.FORGE_APP_KEY }}
safe-outputs:
  github-app:
    client-id: ${{ vars.FORGE_APP_ID }}
    private-key: ${{ secrets.FORGE_APP_KEY }}
  add-comment:
    target: ${{ inputs.thread }}
    target-repo: ${{ inputs.target }}
    max: 1
  push-to-pull-request-branch:
    target: ${{ inputs.pr }}
    target-repo: ${{ inputs.target }}
    required-title-prefix: "[forge] "
    max: 1
---

{{#runtime-import .github/forge/runtime/context.md}}

## Your delivery

Request: issue `${{ inputs.issue }}` in `${{ inputs.target }}`.

You work on the existing plan branch, already checked out in `target/`
(input `ref`: `${{ inputs.ref }}`, plan pull request: `${{ inputs.pr }}`).

Make your changes in `target/`, commit them, and deliver through safe outputs:

1. Call `push_to_pull_request_branch` once to publish your commits to the plan pull request.
2. Then call `add_comment` **exactly once**, on the plan pull request (thread `${{ inputs.thread }}`), with the complete result message defined in your specification
   (the `<!-- infra:result -->` marker, the heading, the YAML block and the body).

If you cannot complete the work, do not push. Call `add_comment` once with `status: blocked`.

{{#runtime-import .github/forge/agents/infra.md}}
