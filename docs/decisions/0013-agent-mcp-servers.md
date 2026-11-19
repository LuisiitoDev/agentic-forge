# ADR-0013: MCP servers for the product agents (GitHub, Atlassian, Azure DevOps)

**Status:** Proposed. Nothing is implemented. The agent workflows are unchanged until this is accepted.

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
| Atlassian | Jira issues, Confluence pages as requirements context | After acceptance |
| Azure DevOps | Work items, repos, pipelines as context | After acceptance |

## Open points (To verify before accepting)

- Whether `gh aw` can declare the Atlassian and Azure DevOps servers (`mcp-servers:`), over which transport,
  and how it restricts tools per server. Not checked against the tool.
- Which official or maintained Atlassian and Azure DevOps MCP servers exist, how they authenticate (OAuth needs
  a person; a non-interactive runner needs a token or service principal), and their read-only scopes.
- Their hosts for the network allowlist, and whether the runner's detection step covers their output.
- Whether per-agent tool lists can be enforced the same way on every engine (Claude, Codex, Copilot), or the
  runner must refuse ([ADR-0002](0002-model-agnostic-runners.md)).
- Which agents need which server. Proposal: BA and Planner read; Coder, Infra and Reviewer only GitHub.

## Consequences

- Agents get richer context without write access leaving the workflow.
- New secrets, hosts and a larger prompt-injection surface (external content), mitigated by 1 to 5.
- Accepting this changes the wording of invariant 4 in AGENTS.md, the runner contract and security doc.
