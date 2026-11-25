# Intake workflow: specification

Specification for the intake of [ADR-0015](decisions/0015-reusable-workflow-intake.md). It is written before the
code. The tests in [.github/forge/tests/test_intake.py](../.github/forge/tests/test_intake.py) follow it. Where a
behaviour is not in this document, it is not specified. Facts about GitHub marked **To verify** are unchecked.

## 1. Purpose

Turn an issue event in a target repository into one `workflow_dispatch` of the engine's orchestrator ([ADR-0031](decisions/0031-least-privilege-dispatch.md)), with the
the dispatch payload below, or into no dispatch at all. It
decides nothing else: the orchestrator re-reads the issue, re-checks the allowlist, the sender and the approval
policy ([security](security-and-governance.md)). Intake is an early filter, **never the security boundary**.

## 2. Parts

| Part | Where | Role |
| --- | --- | --- |
| Caller workflow | Target repository | Triggers on `issues: [labeled]` only ([ADR-0029](decisions/0029-labels-are-the-only-trigger.md)); calls the intake workflow pinned to a tag or SHA. No logic |
| Intake workflow | Engine repository, `on: workflow_call` | Runs the steps below |
| `intake.py` | Engine repository, `.github/forge/` | The decision logic, testable without GitHub. Same style as `orchestrate.py` |

Only `intake.py` is covered by the tests here. The two workflow files are thin: they map GitHub's event context
to the variables in section 3 and call `intake.py`, which calls `decide` and `send`.

## 3. Interface of `intake.py`

The script is sourced and exposes two functions. It sets no options of its own beyond `set -euo pipefail`.

### Inputs (environment)

| Variable | Source in the workflow | Meaning |
| --- | --- | --- |
| `EVENT_NAME` | `github.event_name` | `issues` |
| `ACTION` | `github.event.action` | `labeled` |
| `REPO` | `github.repository` | The target, `owner/name` |
| `SENDER` | `github.event.sender.login` | Who acted |
| `SENDER_TYPE` | `github.event.sender.type` | `User`, `Bot`, ... |
| `ISSUE` | `github.event.issue.number` | Issue number |
| `IS_PR` | `github.event.issue.pull_request != null` | `true` when the "issue" is a pull request |
| `LABEL` | `github.event.label.name` | Label added, for `issues.labeled` |
| `ISSUE_LABELS` | names of `github.event.issue.labels`, one per line | Labels on the issue, for `clarified` |
| `BOT_LOGIN` | engine configuration | The App's bot login, for example `my-forge[bot]`. May be empty |
| `ALLOWED_TARGETS` | engine configuration | `owner/name` values separated by commas or newlines |
| `DELIVERY` | `run-${{ github.run_id }}-${{ github.run_attempt }}` | Id for traceability |

No input carries issue title, body or comment text. Intake never reads them.

### `decide(env)`

Prints exactly one line to stdout and returns 0, whatever the decision:

- `dispatch <payload>`: the payload as compact JSON, or
- `ignore <reason>`: a short reason for the log.

It never prints the input values themselves in the reason, so hostile content cannot reach the log or a
workflow command.

### `send`

Takes the payload and starts the orchestrator with
`gh api -X POST repos/$ENGINE_REPO/actions/workflows/forge-orchestrator.yml/dispatches`, body
`{"ref":"$ENGINE_BRANCH","inputs":{"target","issue","label","sender","delivery"}}`, using `DISPATCH_TOKEN` as the token
(Actions: write is enough). The orchestrator believes none of it on trust: it checks on the issue that `sender` added
`label`. It returns non-zero if
the call fails, so the workflow fails visibly and the event can be re-run. It never prints the token.

## 4. Decision table

Rules apply in this order. The first that matches decides.

| # | Condition | Result |
| --- | --- | --- |
| 1 | `EVENT_NAME/ACTION` is not `issues/labeled` | ignore |
| 2 | Sender is a bot: `SENDER_TYPE` is `Bot`, or `SENDER` ends in `[bot]`, or equals `BOT_LOGIN`, case-insensitive | ignore (loop prevention) |
| 3 | `ALLOWED_TARGETS` has no entry | ignore (fail closed) |
| 4 | `REPO` is not `owner/name` of `[A-Za-z0-9._-]`, or is not in `ALLOWED_TARGETS`, case-insensitive | ignore |
| 5 | `SENDER` does not match `^[A-Za-z0-9-]+(\[bot\])?$` | ignore |
| 6 | `ISSUE` does not match `^[1-9][0-9]*$` | ignore |
| 7 | `IS_PR` is `true` | ignore: a pull request is not a request |
| 8 | `LABEL` is not `forge`, `ready-for-dev` or `clarified` | ignore |
| 9 | `LABEL` is `clarified` and `forge` is not one of `ISSUE_LABELS`, compared by whole line | ignore |
| 10 | Otherwise | dispatch |

Rules 2 to 6 validate before anything is built, as the orchestrator does ("validation before credentials").

### Payload

Exactly these fields, the contract the orchestrator reads, documented in the header of
[forge-orchestrator.yml](../.github/workflows/forge-orchestrator.yml):

| Field | Value |
| --- | --- |
| `event` | `EVENT_NAME` |
| `action` | `ACTION` |
| `target` | `REPO`, as given |
| `issue` | `ISSUE` as a JSON number |
| `sender` | `SENDER` |
| `delivery` | `DELIVERY` |
| `label` | `LABEL`, always present |

## 5. Properties the tests must hold

- **Fail closed:** an empty allowlist, a missing variable or malformed input never dispatches.
- **Whole-value matching:** `forge:claude`, `not-forge` and `forge-2` do not count as `forge`. A target
  `me/app-extra` does not match `me/app`.
- **Injection-safe:** values with spaces, quotes, newlines, `$()` or backticks never reach the payload or the
  reason, and never execute.
- **No content:** the payload has no title, body or comment text.
- **Deterministic:** the same inputs give the same output.
- **Token handling:** `send` uses only `DISPATCH_TOKEN`, to the engine repository only.

## 6. Test matrix

| Id | Case | Expected |
| --- | --- | --- |
| I-01 | `issues/labeled`, label `forge` | dispatch with `label: forge` |
| I-02 | `issues/labeled`, label `ready-for-dev` | dispatch |
| I-03 | Label `bug`, `forge:claude`, `not-forge`, `forge ` | ignore |
| I-04 | `issues/opened`, `issues/unlabeled`, `issue_comment/edited` | ignore |
| I-05 | `issue_comment/created`, even on an issue labeled `forge` | ignore: comments never trigger |
| I-06 | `clarified` on an issue without `forge`, or only `forge:claude` | ignore |
| I-07 | Any label on a pull request | ignore |
| I-08 | Sender type `Bot`; login `x[bot]`; login equal to `BOT_LOGIN` in another case | ignore, all three |
| I-09 | Target not on the allowlist; `me/app-extra` against `me/app`; empty allowlist | ignore |
| I-10 | Allowlist with spaces, commas, newlines and other case | dispatch |
| I-11 | `REPO` malformed: no slash, three parts, with a space | ignore |
| I-12 | `ISSUE` is `abc`, `0`, `007`, `-1`, empty | ignore |
| I-13 | `SENDER` with a space, a quote, `$(id)`, a backtick, a newline | ignore, nothing executed |
| I-14 | Unsupported event `push` | ignore |
| I-15 | Payload has exactly the fields of section 4, `issue` is a number | exact match |
| I-16 | Every ignore is one line starting with `ignore ` and returns 0 | holds |
| I-17 | Reason never contains the hostile input | holds |
| I-18 | `send` calls the dispatch endpoint of `ENGINE_REPO` with event type `forge` and the payload | holds |
| I-19 | `send` fails when `gh` fails | non-zero |
| I-20 | `send` uses `DISPATCH_TOKEN`, not another token | holds |

## 7. The workflows

Both exist and are covered by static checks in [test_workflow.py](../.github/forge/tests/test_workflow.py)
(parse, no permissions, no `${{ }}` inside `run`, actions pinned to a SHA, no persisted credentials, caller has
no logic, the shipped allowlist is empty).

- **Caller template:** [.github/forge/templates/forge-caller.yml](../.github/forge/templates/forge-caller.yml).
  Copied into a target as `.github/workflows/forge.yml`. It is outside `.github/workflows/` so it never runs
  here. It pins the intake workflow and passes the same pin as `engine-ref`.
- **Intake workflow:** [.github/workflows/forge-intake.yml](../.github/workflows/forge-intake.yml), `workflow_call`
  with the input `engine-ref` and the secret `dispatch-token`. It checks out `.github/forge` of the engine at
  `engine-ref`, maps the event to section 3 through `env`, runs `decide` and, on `dispatch`, `send`.
- **Allowlist:** [.github/forge/targets.txt](../.github/forge/targets.txt), one `owner/name` per line, `#`
  comments. Shipped empty, so nothing is forwarded until a target is added. The orchestrator keeps its own
  `FORGE_ALLOWED_TARGETS`; the two lists must be equal.
- **Bot login:** intake passes none. The App's bot is always `Bot` type, which rule 2 already ignores.

## 8. Open points (To verify, they can change this spec)

1. In a called workflow, `github.event` and `github.repository` are the caller's, but `vars` and `secrets` also
   resolve against the caller. The allowlist is therefore a file in the engine repository (section 7). Whether
   that premise holds is itself unverified. The authoritative check stays in the orchestrator.
2. The intake checks out the engine at `engine-ref`, which the caller must set equal to its pin; nothing
   enforces that. Whether the called workflow can read its own ref (for example `job_workflow_sha`) instead is
   unverified. The checkout uses the dispatch token, so for a private engine that token needs read access too.
3. That `github.event.sender.type` and `issue.pull_request` are present for these events, and how they behave on
   forks.
4. Whether a caller can alter `github.repository` or the actor seen by the called workflow.
5. Whether `DELIVERY` as a run id is enough for traceability, since there is no webhook delivery id.

## 9. Definition of done

- `intake.py` passes every case in section 6.
- `test_intake.py` is part of `python3 .github/forge/tests/run.py`.
- The orchestrator tests (`python3 .github/forge/tests/run.py`) still pass.
- The intake and caller workflows exist and follow section 7 (done, unverified on GitHub).
- Section 8 is verified or each item has a recorded workaround.
- Said plainly: it has not run end to end against GitHub until it has.
