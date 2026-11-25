# ADR-0029: Labels are the only trigger

**Status:** Accepted. Amends [ADR-0015](0015-reusable-workflow-intake.md) (the caller's triggers) and the intake
spec. Implemented and tested with a fake `gh`; not run against GitHub.

## Context

The caller in each target listened to `issues: labeled` and `issue_comment: created`. A comment resumed a request
waiting for clarification. So every comment on every issue of a target started an Actions run in that target, which
the target pays for and which almost always ended in "ignore". The engine also had two kinds of human event to
validate, and a comment from anyone with write access resumed the request, even one that answered nothing.

## Decision

1. **The caller triggers on `issues: labeled` only.** Comments start nothing.
2. **A new human label, `clarified`, resumes a request** waiting in `needs-clarification`. The person answers in a
   comment, then adds `clarified`. The agent reads every comment, as before.
3. **`clarified` is a signal, not a state.** The orchestrator removes it at once, so the next answer can add it again.
   On a request that is not waiting, it is removed and nothing runs.
4. When an agent asks questions, the orchestrator posts how to continue: answer in a comment, then add `clarified`.
5. The engine creates `clarified` with its other labels. The intake forwards `forge`, `ready-for-dev` and `clarified`;
   `clarified` only on an issue labeled `forge`.

## Consequences

- Targets run no workflow for comments, and the caller is one trigger shorter.
- Resuming is an explicit act, checked like any label: a human with write access.
- A caller pinned to an older engine still sends comment events. The intake it pins forwards them, and the
  orchestrator now ignores them, so such a target must update its caller to resume requests.
