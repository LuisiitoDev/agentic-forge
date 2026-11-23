# Runner contract

> **Implementation note ([ADR-0009](decisions/0009-github-agentic-workflows.md)).** In the current engine the
> "runner" is a `gh aw` engine, fixed when each agent workflow is compiled. `gh aw` provides the read-only agent
> job, the tool restrictions, the turn and time limits and the safe outputs, so most of the rules below are met by
> the platform rather than by a script of ours. The contract is kept as the provider-neutral target any engine
> must meet. The Phase 1 engine is Copilot, authenticated with `COPILOT_GITHUB_TOKEN` ([ADR-0017](decisions/0017-copilot-engine-for-phase-1.md)). Claude authenticates with `ANTHROPIC_API_KEY`; subscription OAuth tokens are unsupported.

A **runner** adapts one model provider to the pipeline. It is the only place provider vocabulary may appear.
The contract is deliberately the same shape as a function from request to result, so a runner can later be
re-implemented behind a runtime port without changing agents or workflows.

## Request

| Field | Meaning |
|---|---|
| `agent` | Agent name, resolved to its [spec](agent-spec.md) |
| `prompt` | Full prompt file: instructions, runtime rules, issue and PR data |
| `capabilities` | Neutral capabilities the agent may use: `read`, `search`, `edit`, `shell` |
| `limits` | `max_turns` and `timeout_minutes` |
| `model` | Optional model override; otherwise the provider default |
| `workdir` | The repository checkout the agent operates in |

## Result

| Output | Meaning |
|---|---|
| `result.md` | The agent's final message, verbatim. It becomes the result comment |
| exit code | `0` success. Non-zero means failure, including "no result produced" |

A run that exits `0` with an empty `result.md` is a failure.

## Rules every runner must follow

1. **Fail closed on capabilities.** If the provider cannot enforce a requested restriction (for example `edit`
   without `shell`), exit non-zero before invoking the model. Never run with broader permissions than asked.
2. **No GitHub write access.** The runner must not expose the GitHub token to the agent or its tools. Read-only
   tools declared in the workflow, including MCP servers ([ADR-0013](decisions/0013-agent-mcp-servers.md)), are allowed.
3. **Honour limits.** Enforce `max_turns` where the provider supports it and always enforce the timeout.
4. **No secrets in output.** Do not echo credentials or provider error bodies into `result.md` or logs.
5. **One secret per provider, optional at the workflow level.** A runner validates only its own secret and
   fails with a clear message if it is missing.
6. **Pin the tool version.** Install the provider CLI at an explicit version.

## Capability mapping

Each runner owns the translation from neutral capabilities to the provider's permission model.

| Capability | Meaning | Claude | Codex | Copilot |
|---|---|---|---|---|
| `read` | Read files in the checkout | To verify | To verify | To verify |
| `search` | Search the checkout | To verify | To verify | To verify |
| `edit` | Create and modify files | To verify | To verify | To verify |
| `shell` | Run commands | To verify | To verify | To verify |

Filling this table, with the exact flags or config for each provider, is part of the work for each runner. A
cell may legitimately end as "cannot be enforced"; the runner then refuses the corresponding agents.

## Providers

| Runner | Tool | Secret | Status |
|---|---|---|---|
| `claude` | Claude Code CLI | OAuth token | Mapping known from the previous pipeline; to re-verify |
| `codex` | Codex CLI | API key or ChatGPT login | **To verify** |
| `copilot` | Copilot CLI | `COPILOT_GITHUB_TOKEN` | Phase 1 engine ([ADR-0017](decisions/0017-copilot-engine-for-phase-1.md)). `gh aw` compiles it with `--allow-tool` restrictions; behaviour on a real run and the token type are **To verify** |

## Relation to the .NET runtime

The previous runtime defined `IAgent.InvokeAsync(request, ct)` returning success, failure, needs-more-input or
cancelled. The runner contract is its pipeline-level counterpart. Keeping them aligned is what makes the
migration in [ADR-0004](decisions/0004-runtime-as-future-engine.md) a swap of implementation, not a rewrite.

## Rate limits (Copilot)

A `429 Too Many Requests` from a runner is never answered by switching to another model. There is no fallback list:
other models are refused on the current account anyway ([ADR-0017](decisions/0017-copilot-engine-for-phase-1.md)).
The stage ends as `blocked`, and a person re-runs it after the `retry-after` time.
