# ADR-0019: The Reviewer reads CI results and security alerts

**Status:** Accepted for the Reviewer workflow. Compiled and guarded by tests; not run against GitHub.
Extends [ADR-0013](0013-agent-mcp-servers.md) (read-only tools for agents) to the Reviewer.

## Context

The Reviewer spec ([agent spec](../../.github/forge/agents/reviewer.md)) asks it to verify the build and tests itself,
and to check security: unsafe input, secrets in code, dependency and permission changes. Its GitHub tools were the
issue and pull request toolsets only, so it could not see the target's CI runs, code scanning alerts, dependency
alerts or secret scanning alerts. Those are evidence a person reviewing the same pull request would look at.

## Decision

1. The Reviewer's GitHub tool adds the toolsets `repos`, `actions`, `code_security`, `dependabot` and
   `secret_protection`, next to `issues` and `pull_requests`. Nothing else changes for the other four agents.
2. **Read-only stays absolute** (invariant 4). The compiled workflow runs the GitHub server with `GITHUB_READ_ONLY=1`
   and lists 35 tools, none that writes. A static test checks every agent's lock file for the read-only flag and for
   any tool whose name is a write operation, because the `actions` toolset contains operations that start, cancel
   or delete runs in the full GitHub server.
3. The workflow token gets the matching read permissions (`actions`, `security-events`, `vulnerability-alerts`).
4. The GitHub App needs the same reads on the target: Actions, Code scanning alerts, Dependabot alerts and Secret
   scanning alerts, all read-only. The manifest and the setup guides list them.
5. The Reviewer uses these as **evidence next to its own checks, never instead of them**. A source it cannot read
   is `not-run`, which the spec already treats as a finding. So a missing permission degrades the review; it does
   not break it.

## Considered and rejected

- **`web-fetch` for the Planner** (to read documentation linked in a request). `gh aw` rejects it with the Copilot
  engine ("offline BYOK mode disables web tools"). A fetch MCP server would work but adds a supply chain and a
  prompt injection path from request text to the open web. Not added.
- **Persistent agent memory** (`cache-memory`, `repo-memory`). Hidden state between runs against the principle that
  a step's inputs and outputs are explicit. Not added.
- **A browser (Playwright) for the Reviewer.** Useful for a web target such as a React app, but it needs a running
  app and more network. Decide when a target needs it.

## To verify

- That the App manifest accepts the permission keys `actions`, `security_events`, `vulnerability_alerts` and
  `secret_scanning_alerts` (read). The pre-flight check cannot see the installed permissions.
- That the tools return data with these permissions on a real target, and that the Reviewer reports `not-run` when
  they are missing.
- That the target's repository has the features on (code scanning, Dependabot, secret scanning): GitHub returns an
  error for a repository that does not.

## Consequences

- The Reviewer's evidence is closer to a human reviewer's. The Reviewer prompt grows by the tool descriptions.
- The App asks for four more read permissions. They grant no write access, but an owner of a target sees a longer
  permission list when installing it.
