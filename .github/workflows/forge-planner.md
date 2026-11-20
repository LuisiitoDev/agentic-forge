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
network:
  allowed:
    - defaults
    - node
    - mcp.atlassian.com
    - dev.azure.com
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
mcp-servers:
  # ADR-0013. Read-only context for this agent. Server URLs, tool names and authentication are To verify
  # against the real servers (docs/first-run-checklist.md). Writes never go through here.
  atlassian:
    url: "https://mcp.atlassian.com/v1/sse"
    headers:
      Authorization: ${{ secrets.FORGE_ATLASSIAN_AUTH }}
    allowed: [getJiraIssue, searchJiraIssuesUsingJql, getConfluencePage]
  azure-devops:
    command: npx
    args: ["-y", "@azure-devops/mcp", "${{ vars.FORGE_ADO_ORG }}"]
    env:
      AZURE_DEVOPS_EXT_PAT: ${{ secrets.FORGE_ADO_PAT }}
    allowed: [wit_get_work_item, search_workitem]
safe-outputs:
  github-app:
    client-id: ${{ vars.FORGE_APP_ID }}
    private-key: ${{ secrets.FORGE_APP_KEY }}
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
   (the `<!-- planner:result -->` marker, the heading, the YAML block and the body).

You may only change files under `.forge/requests/${{ inputs.issue }}/`.

{{#runtime-import .github/forge/agents/planner.md}}
