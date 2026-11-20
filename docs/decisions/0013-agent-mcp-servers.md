# ADR-0013: MCP servers for the product agents (GitHub, Atlassian, Azure DevOps)

**Status:** Accepted for the declaration in BA and Planner (`forge-ba.md`, `forge-planner.md`). Not run against GitHub or
the real servers. Atlassian tool names and authentication, and the Azure DevOps token scope, are **To verify**
(see the open points).

## Context

The product agents (BA, Planner, Coder, Infra, Reviewer) would benefit from context that lives outside the
request issue: related issues and PRs (GitHub), Jira issues and Confluence pages (Atlassian), and work items,
repos and pipelines (Azure DevOps). The MCP is the usual way to give an agent those tools.

This touches invariant 4 ("Agents have no GitHub access", [AGENTS.md](../../AGENTS.md),
[runner contract](../runner-contract.md), [security](../security-and-governance.md)). Each agent workflow
already declares `tools: github:` with read toolsets (`issues`, `pull_requests`), so the invariant today means
*no write access and no token exposed to the agent*, not *no read tool*. This ADR makes that reading explicit
and extends it to the other two servers.

## Decision (proposed)

1. **Read-only by default.** An MCP server may be given to an agent only with read tools. Writes stay in the
   workflow (`safe-outputs`, orchestrator), as today ([ADR-0009](0009-github-agentic-workflows.md)).
2. **Allowlisted tools, per agent.** Each agent spec names the capability it needs; the workflow lists the exact
   tools of each server. A server with no tool in the list is not started. Provider tool names appear only in
   the workflow, never in the neutral agent spec ([ADR-0003](0003-neutral-agent-spec.md)).
3. **Credentials are engine configuration, scoped and read-only.** One credential per server, held as an engine
   secret, with the minimum read scope. The GitHub App token for the target is not reused for them. A target's
   `.forge/config.yml` cannot add a server or widen tools ([ADR-0006](0006-target-access-and-config.md)).
4. **Output is untrusted.** Content returned by a server (issues, pages, work items) is data, like the request.
5. **Fail closed.** A server that cannot be started or authenticated stops the agent with `blocked`; it does not
   fall back to running without it silently.
6. **Network.** Each server's hosts are added to that workflow's `network.allowed`, and only there.

| Server | Gives agents | Phase |
| --- | --- | --- |
| GitHub | Related issues, PRs, code search in the target | Already declared; formalised here |
| Atlassian | Jira issues, Confluence pages as requirements context | Declared in BA and Planner |
| Azure DevOps | Work items and work item search as context | Declared in BA and Planner |

## Writing back (workflow, never the agent)

Read access is the agent's; **write-back is the engine's**. The orchestrator or a `safe-outputs` step updates
the tracker from the agent's result and an allowlist, the same way it labels and comments on GitHub today:

| Tracker | Write-back (by the workflow) |
| --- | --- |
| GitHub issues | State labels, result comments, plan PR (as today) |
| Jira | Comment with the plan PR link; optional transition on delivery |
| Azure DevOps | Comment with the plan PR link; optional work item state change on delivery |

GitHub issues stay the **system of record** for the request and its state ([request contract](../request-contract.md)).
Jira and Azure DevOps items are **linked references** (a key or URL in the issue) that agents may read for
context and that the workflow may update. A write to Jira or Azure DevOps never gates the pipeline: if it fails,
the request continues and a comment records the failure.

## What is declared and what was checked

Declared in `forge-ba.md` and `forge-planner.md` only; Coder, Infra and Reviewer get no extra server.

| Server | Declaration | Checked | Not checked |
| --- | --- | --- | --- |
| Azure DevOps | `npx @azure-devops/mcp@2.11.0 <org> --authentication envvar`, token in `ADO_MCP_AUTH_TOKEN` from the engine secret `FORGE_ADO_PAT`, org from variable `FORGE_ADO_ORG`. Tools: `wit_work_item`, `wit_query`, `search_workitem` | Package exists on npm; tool names and the `envvar` login read from the 2.11.0 source. Write tools (`wit_work_item_write`, `*_comment_write`, `*_link_write`, attachments) are not allowed | Token scope (read-only PAT), org access, behaviour in the runner |
| Atlassian | `https://mcp.atlassian.com/v1/sse`, header `Authorization` from the engine secret `FORGE_ATLASSIAN_AUTH`. Tools: `getJiraIssue`, `searchJiraIssuesUsingJql`, `getConfluencePage` | The endpoint answers 401 without credentials | Tool names, whether the endpoint is the right one, how a non-interactive runner authenticates, read-only scope |

`gh aw` v1.0.1 compiles both declarations and records only the allowed tools per server. A new secret triggers its
safe-update gate; the two secrets above were reviewed and approved with `gh aw compile --approve`. The only other
additions in the compiled workflows are a pinned `actions/setup-node` and the domains of the two servers and npm.

## Open points (To verify)

- ~~Whether `gh aw` can declare the servers and restrict tools~~ Checked: it can (see above).
- How the Atlassian server authenticates in a non-interactive runner (OAuth needs a person; a token must be
  accepted), and the read-only scopes of both credentials.
- Their hosts for the network allowlist, and whether the runner's detection step covers their output.
- Whether per-agent tool lists can be enforced the same way on every engine (Claude, Codex, Copilot), or the
  runner must refuse ([ADR-0002](0002-model-agnostic-runners.md)).
- Which agents need which server. Proposal: BA and Planner read; Coder, Infra and Reviewer only GitHub.

## Consequences

- Agents get richer context without write access leaving the workflow.
- New secrets, hosts and a larger prompt-injection surface (external content), mitigated by 1 to 5.
- Invariant 4 in AGENTS.md, the runner contract and the security doc now say "no write access", and allow
  read-only tools declared in the agent workflows.
