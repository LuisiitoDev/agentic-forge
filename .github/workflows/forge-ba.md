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
permissions:
  contents: read
  issues: read
  pull-requests: read
concurrency:
  job-discriminator: ${{ inputs.target }}#${{ inputs.issue }}
engine: claude
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
tools:
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
---

{{#runtime-import .github/forge/runtime/context.md}}

## Your delivery

Request: issue `${{ inputs.issue }}` in `${{ inputs.target }}`.

The pull request does not exist yet, so you post on the request issue (thread `${{ inputs.thread }}`).

Deliver your result by calling the `add_comment` safe output **exactly once**. The comment body is the complete
result message defined in your specification (the `<!-- ba:result -->` marker, the heading, the YAML block and
the body). You have no other safe output.

{{#runtime-import .github/forge/agents/ba.md}}
