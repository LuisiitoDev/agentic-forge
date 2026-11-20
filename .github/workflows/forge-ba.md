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
network:
  allowed:
    - defaults
    - node
    - mcp.atlassian.com
    - dev.azure.com
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
mcp-servers:
  # ADR-0013. Read-only context for this agent. Server URLs, tool names and authentication are To verify
  # against the real servers (docs/first-run-checklist.md). Writes never go through here.
  # Azure DevOps: tool names, `--authentication envvar` and ADO_MCP_AUTH_TOKEN read from @azure-devops/mcp 2.11.0 source.
  # Atlassian: endpoint answers 401 without credentials; tool names and auth are not yet verified.
  atlassian:
    url: "https://mcp.atlassian.com/v1/sse"
    headers:
      Authorization: ${{ secrets.FORGE_ATLASSIAN_AUTH }}
    allowed: [getJiraIssue, searchJiraIssuesUsingJql, getConfluencePage]
  azure-devops:
    command: npx
    args: ["-y", "@azure-devops/mcp@2.11.0", "${{ vars.FORGE_ADO_ORG }}", "--authentication", "envvar"]
    env:
      ADO_MCP_AUTH_TOKEN: ${{ secrets.FORGE_ADO_PAT }}
    allowed: [wit_work_item, wit_query, search_workitem]
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
