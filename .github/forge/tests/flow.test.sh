#!/usr/bin/env bash
# Decision-flow tests for the orchestrator, with a fake `gh` (tests/bin/gh) standing in for GitHub.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
FAKE_DIR="$(mktemp -d)"; trap 'rm -rf "$FAKE_DIR"' EXIT
export PATH="$ROOT/.github/forge/tests/bin:$PATH" FAKE_DIR
pass=0; fail=0
cm() { jq -n --arg b "$2" --arg t "$3" --arg u "${4:-forge[bot]}" '{body:$b, created_at:$t, user:{login:$u}}'; }
result() { local t; case "$1" in ba) t=BA ;; *) t="${1^}" ;; esac
  printf '## %s result: %s\n\n```yaml\nstatus: %s\ntasks:\n  app: %s\n  infra: %s\n```\n' "$t" "$2" "$2" "${3:-1}" "${4:-0}"; }
run() { # name; env via exports; prints dispatches, labels, posted
  rm -rf "$FAKE_DIR"; mkdir -p "$FAKE_DIR"; : > "$FAKE_DIR/labels.txt"
  printf '%s\n' $LABELS > "$FAKE_DIR/labels.txt" 2>/dev/null || true
  echo "${COMMENTS:-[]}" > "$FAKE_DIR/comments.json"
  echo "${PULLS:-[]}" > "$FAKE_DIR/pulls.json"
  [ -n "${PR_COMMENTS:-}" ] && echo "$PR_COMMENTS" > "$FAKE_DIR/pr_comments.json"
  echo "${FILES:-[]}" > "$FAKE_DIR/files.json"
  touch "$FAKE_DIR/dispatch.log" "$FAKE_DIR/posted.txt"
  ( export TARGET=o/r ISSUE=12 TARGET_TOKEN=t ENGINE_TOKEN=e ENGINE_REPO=eng/forge ENGINE_REF=main FORGE_BOT_LOGIN="forge[bot]" RUN_URL=http://run
    source "$ROOT/.github/forge/orchestrate.sh"; main ) >/dev/null 2>"$FAKE_DIR/err.log"
}
check() { # name expect_dispatch_substring expect_labels(sorted, space-sep) [expect_posted_substring]
  local d l p; d=$(cat "$FAKE_DIR/dispatch.log"); l=$(sort "$FAKE_DIR/labels.txt" | xargs); p=$(cat "$FAKE_DIR/posted.txt")
  local ok=1
  [ -z "$2" ] && [ -n "$d" ] && ok=0
  [ -n "$2" ] && ! grep -q -- "$2" <<<"$d" && ok=0
  [ "$l" != "$3" ] && ok=0
  [ -n "${4:-}" ] && ! grep -q -- "$4" <<<"$p" && ok=0
  [ -f "$FAKE_DIR/unhandled.log" ] && { ok=0; cat "$FAKE_DIR/unhandled.log"; }
  if [ $ok = 1 ]; then pass=$((pass+1)); else fail=$((fail+1)); echo "FAIL $1"; echo "  dispatch: $d"; echo "  labels: [$l]"; echo "  posted: $p"; fi
}
export FORGE_KIND=agent RUN_STARTED_AT="2026-11-18T09:00:00Z"

# 1 BA complete -> planning + dispatch planner
LABELS="forge" AGENT=ba CONCLUSION=success COMMENTS="[$(cm x "$(result ba complete)" 2026-11-18T10:00:00Z)]" run
check "ba complete" "forge-planner-claude.yml" "forge planning"
# 2 BA needs clarification
LABELS="forge" AGENT=ba CONCLUSION=success COMMENTS="[$(cm x "$(result ba needs-clarification)" 2026-11-18T10:00:00Z)]" run
check "ba clarif" "" "forge needs-clarification"
# 3 planner ready with PR -> spec-review
LABELS="forge planning ready-for-dev" AGENT=planner CONCLUSION=success PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(result planner ready-for-review 2 1)" 2026-11-18T10:00:00Z)]" run
check "planner ready" "" "forge spec-review"
# 4 planner ready but no PR -> blocked
LABELS="forge planning" AGENT=planner CONCLUSION=success PULLS='[]' COMMENTS="[$(cm x "$(result planner ready-for-review)" 2026-11-18T10:00:00Z)]" run
check "planner no pr" "" "blocked forge" "Blocked"
# 5 stale result (older than run start) -> blocked
LABELS="forge planning" AGENT=ba CONCLUSION=success COMMENTS="[$(cm x "$(result ba complete)" 2026-11-18T08:00:00Z)]" run
check "stale" "" "blocked forge" "no result"
# 6 untrusted author result ignored -> blocked
LABELS="forge" AGENT=ba CONCLUSION=success COMMENTS="[$(cm x "$(result ba complete)" 2026-11-18T10:00:00Z mallory)]" run
check "forged result" "" "blocked forge" "no result"
# 6b the heading of another agent is not this agent's result (a Planner heading is not the BA's)
LABELS="forge" AGENT=ba CONCLUSION=success COMMENTS="[$(cm x "$(result planner ready-for-review)" 2026-11-18T10:00:00Z)]" run
check "other agent heading" "" "blocked forge" "no result"
# 6c a result text quoted inside a longer comment is not a result: it must be the first line
LABELS="forge" AGENT=ba CONCLUSION=success COMMENTS="[$(cm x "Some note first
$(result ba complete)" 2026-11-18T10:00:00Z)]" run
check "heading must be first" "" "blocked forge" "no result"
# 7 failed run -> blocked
LABELS="forge planning" AGENT=coder CONCLUSION=failure run
check "failure" "" "blocked forge" "ended with"
# 8 the engine has no infrastructure agent (ADR-0023): a coder result never leads to one, whatever the plan said
LABELS="forge" AGENT=coder CONCLUSION=success PULLS='[{"number":7}]' FILES='[{"filename":".forge/requests/12/plan.md"}]' COMMENTS="[$(cm x "$(result planner ready-for-review 2 1)" 2026-11-18T08:30:00Z),$(cm x "$(result coder ready-for-review)" 2026-11-18T10:00:00Z)]" run
check "coder->review even if the plan listed infra" "forge-reviewer-claude.yml" "forge"
if grep -q "forge-infra" "$FAKE_DIR/dispatch.log"; then fail=$((fail+1)); echo "FAIL infra was dispatched"; else pass=$((pass+1)); fi
# 9 coder ready, no infra, files in plan dir only -> reviewer
LABELS="forge" AGENT=coder CONCLUSION=success PULLS='[{"number":7}]' FILES='[{"filename":".forge/requests/12/plan.md"}]' COMMENTS="[$(cm x "$(result planner ready-for-review 2 0)" 2026-11-18T08:30:00Z),$(cm x "$(result coder ready-for-review)" 2026-11-18T10:00:00Z)]" run
check "coder->review" "forge-reviewer-claude.yml" "forge"
# 10 coder changed a file outside any allowed path (no config) -> blocked, no review
LABELS="forge" AGENT=coder CONCLUSION=success PULLS='[{"number":7}]' FILES='[{"filename":"src/x.cs"}]' COMMENTS="[$(cm x "$(result planner ready-for-review 2 0)" 2026-11-18T08:30:00Z),$(cm x "$(result coder ready-for-review)" 2026-11-18T10:00:00Z)]" run
check "scope gate" "" "blocked forge" "src/x.cs"
# 11 reviewer pass
LABELS="forge" AGENT=reviewer CONCLUSION=success COMMENTS="[$(cm x "$(result reviewer pass)" 2026-11-18T10:00:00Z)]" run
check "reviewer pass" "" "forge needs-review"
# 12 reviewer changes-requested -> replan, approval removed
LABELS="forge ready-for-dev" AGENT=reviewer CONCLUSION=success PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(result reviewer changes-requested)" 2026-11-18T10:00:00Z)]" run
check "replan" "forge-planner-claude.yml" "forge planning"
# 13 reviewer cannot ask for clarification -> unknown status blocked
LABELS="forge" AGENT=reviewer CONCLUSION=success COMMENTS="[$(cm x "$(result reviewer needs-clarification)" 2026-11-18T10:00:00Z)]" run
check "reviewer odd status" "" "blocked forge" "unknown status"

export FORGE_KIND=human P_EVENT=issues P_ACTION=labeled P_SENDER=alice
# 14 forge label start
LABELS="forge" P_LABEL=forge run
check "start" "forge-ba-claude.yml" "forge"
# 15 start is idempotent when in progress
LABELS="forge planning" P_LABEL=forge run
check "start idempotent" "" "forge planning"
# 16 bot sender ignored
LABELS="forge" P_LABEL=forge USER_TYPE=Bot run
check "bot ignored" "" "forge"
# 17 read-only sender ignored
LABELS="forge" P_LABEL=forge PERM=read run
check "read perm ignored" "" "forge"
# 18 approve ok -> coder
LABELS="forge spec-review ready-for-dev" P_LABEL=ready-for-dev PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(result planner ready-for-review 2 0)" 2026-11-18T08:30:00Z)]" run
check "approve" "forge-coder-claude.yml" "forge ready-for-dev"
# 18b ADR-0023: a plan that still lists infrastructure tasks is not run, and the approval is refused with the reason
LABELS="forge spec-review ready-for-dev" P_LABEL=ready-for-dev PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(result planner ready-for-review 2 1)" 2026-11-18T08:30:00Z)]" run
check "plan with infra tasks is refused" "" "forge spec-review" "does not run infrastructure work"
LABELS="forge spec-review ready-for-dev" P_LABEL=ready-for-dev PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(result planner ready-for-review 0 1)" 2026-11-18T08:30:00Z)]" run
check "plan with only infra tasks is refused" "" "forge spec-review" "a person does it"
# 18c a plan with no application task is blocked, with the new wording
LABELS="forge spec-review ready-for-dev" P_LABEL=ready-for-dev PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(result planner ready-for-review 0 0)" 2026-11-18T08:30:00Z)]" run
check "plan without application tasks" "" "blocked forge ready-for-dev" "no application tasks"
# 19 CODEOWNERS is not consulted (ADR-0011): approval by a writer proceeds. If the orchestrator read it, the fake
#    gh would log an unhandled call and this test would fail.
LABELS="forge spec-review ready-for-dev" P_LABEL=ready-for-dev PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(result planner ready-for-review 2 0)" 2026-11-18T08:30:00Z)]" run
check "codeowners not consulted" "forge-coder-claude.yml" "forge ready-for-dev"
# 20 approve when not awaiting approval -> refused
LABELS="forge ready-for-dev" P_LABEL=ready-for-dev run
check "not awaiting" "" "forge" "not awaiting"
# 21 approve by read-only user -> ignored, label stays (workflow job only logs)
LABELS="forge spec-review ready-for-dev" P_LABEL=ready-for-dev PERM=read run
check "approve no perm" "" "forge ready-for-dev spec-review"
# 22 comment resumes BA after clarification
export P_EVENT=issue_comment P_ACTION=created
LABELS="forge needs-clarification" COMMENTS="[$(cm x "$(result ba needs-clarification)" 2026-11-18T08:00:00Z)]" run
check "resume ba" "forge-ba-claude.yml" "forge"
# 23 comment when not waiting -> nothing
LABELS="forge planning" run
check "comment ignored" "" "forge planning"

export FORGE_KIND=agent RUN_STARTED_AT="2026-11-18T09:00:00Z"
unset P_EVENT P_ACTION P_LABEL P_SENDER
# --- results on the plan pull request (shared PR) ---
# 24 Coder result posted on the PR is found, and review is dispatched with the PR as the thread
LABELS="forge" AGENT=coder CONCLUSION=success PULLS='[{"number":7}]' FILES='[{"filename":".forge/requests/12/plan.md"}]' \
  COMMENTS="[$(cm x "$(result planner ready-for-review 2 0)" 2026-11-18T08:30:00Z)]" \
  PR_COMMENTS="[$(cm x "$(result coder ready-for-review)" 2026-11-18T10:00:00Z)]" run
check "coder result on PR" "forge-reviewer-claude.yml.*thread=7" "forge"
# 25 Reviewer result on the PR (pass)
LABELS="forge" AGENT=reviewer CONCLUSION=success PULLS='[{"number":7}]' PR_COMMENTS="[$(cm x "$(result reviewer pass)" 2026-11-18T10:00:00Z)]" run
check "reviewer on PR" "" "forge needs-review"
# 26 A look-alike result on the PR by a non-bot is ignored
LABELS="forge" AGENT=reviewer CONCLUSION=success PULLS='[{"number":7}]' PR_COMMENTS="[$(cm x "$(result reviewer pass)" 2026-11-18T10:00:00Z mallory)]" run
check "forged on PR" "" "blocked forge" "no result"
# 27 Replan: planner result posted on the PR is read, approval cleared
LABELS="forge planning" AGENT=planner CONCLUSION=success PULLS='[{"number":7}]' PR_COMMENTS="[$(cm x "$(result planner ready-for-review 1 0)" 2026-11-18T10:00:00Z)]" run
check "replan result on PR" "" "forge spec-review"
# 28 BA answers on the issue: planner is dispatched with the ISSUE as the thread (no PR exists yet)
LABELS="forge" AGENT=ba CONCLUSION=success COMMENTS="[$(cm x "$(result ba complete)" 2026-11-18T10:00:00Z)]" run
check "ba thread is issue" "forge-planner-claude.yml.*thread=12" "forge planning"
# 29 Reviewer asks for changes: the replan is dispatched on the PR thread
LABELS="forge ready-for-dev" AGENT=reviewer CONCLUSION=success PULLS='[{"number":7}]' PR_COMMENTS="[$(cm x "$(result reviewer changes-requested)" 2026-11-18T10:00:00Z)]" run
check "replan thread is PR" "forge-planner-claude.yml.*thread=7" "forge planning"
# 30 Approval dispatches the coder on the PR thread
export FORGE_KIND=human P_EVENT=issues P_ACTION=labeled P_SENDER=alice P_LABEL=ready-for-dev
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(result planner ready-for-review 2 0)" 2026-11-18T08:30:00Z)]" run
check "coder thread is PR" "forge-coder-claude.yml.*thread=7" "forge ready-for-dev"

# ---- approval policy (ADR-0012): no self-approval, sensitive paths need admin or maintain ----
planp() { # paths... -> a planner result whose YAML lists those paths
  local out=$'## Planner result: ready-for-review\n\n```yaml\nstatus: ready-for-review\ntasks:\n  app: 1\n  infra: 0\npaths:\n' p
  for p in "$@"; do out+="  - $p"$'\n'; done
  out+='```'$'\n'; printf '%s' "$out"
}
approval() { # sets the common approval scenario; callers add env and a planner result
  export FORGE_KIND=human P_EVENT=issues P_ACTION=labeled P_SENDER=alice P_LABEL=ready-for-dev
}
approval
PLAN_OK="[$(cm x "$(planp src/a.cs)" 2026-11-18T08:30:00Z)]"

# 31 self-approval refused
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="$PLAN_OK" REQUESTER=alice run
check "self-approval refused" "" "forge spec-review" "You opened this request"
# 32 self-approval allowed by the engine setting
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="$PLAN_OK" REQUESTER=alice FORGE_ALLOW_SELF_APPROVAL=true run
check "self-approval allowed" "forge-coder-claude.yml" "forge ready-for-dev"
# 33 requester compared case-insensitively
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="$PLAN_OK" REQUESTER=ALICE run
check "self-approval case" "" "forge spec-review" "You opened this request"
# 34 cannot tell who opened the request -> denied
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="$PLAN_OK" REQUESTER_FAIL=1 run
check "requester unknown" "" "forge spec-review" "Could not tell who opened"

export FORGE_SENSITIVE_PATHS="infra/,.github/workflows/"
# 35 sensitive path declared in the plan, approver only has write
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(planp src/a.cs infra/main.bicep)" 2026-11-18T08:30:00Z)]" run
check "sensitive plan path, write" "" "forge spec-review" "admin or maintain"
# 36 sensitive path only in the real PR files, not declared in the plan
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' FILES='[{"filename":".github/workflows/ci.yml"}]' COMMENTS="$PLAN_OK" run
check "sensitive PR file, write" "" "forge spec-review" "ci.yml"
# 37 a maintainer may approve
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(planp infra/main.bicep)" 2026-11-18T08:30:00Z)]" ROLE=maintain run
check "sensitive, maintain" "forge-coder-claude.yml" "forge ready-for-dev"
# 38 an admin may approve
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(planp infra/main.bicep)" 2026-11-18T08:30:00Z)]" PERM=admin run
check "sensitive, admin" "forge-coder-claude.yml" "forge ready-for-dev"
# 39 a plan with no sensitive path needs only write access
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="$PLAN_OK" run
check "not sensitive, write" "forge-coder-claude.yml" "forge ready-for-dev"
# 40 whole segments only: infra-docs/ is not infra/
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(planp infra-docs/readme.md)" 2026-11-18T08:30:00Z)]" run
check "segment boundary" "forge-coder-claude.yml" "forge ready-for-dev"
# 41 case-insensitive: INFRA/ is infra/
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(planp INFRA/main.bicep)" 2026-11-18T08:30:00Z)]" run
check "case-insensitive" "" "forge spec-review" "admin or maintain"
# 42 a path that climbs out of its prefix is a hit
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(planp src/../infra/x.bicep)" 2026-11-18T08:30:00Z)]" run
check "traversal" "" "forge spec-review" "admin or maintain"
# 43 the role cannot be read -> denied
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(planp infra/main.bicep)" 2026-11-18T08:30:00Z)]" ROLE_FAIL=1 run
check "role unknown" "" "forge spec-review" "unknown"
# 44 the PR files cannot be listed -> denied
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="$PLAN_OK" FILES_FAIL=1 run
check "files unknown" "" "forge spec-review" "Could not list"
# 45 a list with spaces and newlines is read
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(planp infra/x.bicep)" 2026-11-18T08:30:00Z)]" FORGE_SENSITIVE_PATHS=" docs/ , infra/ " run
check "list parsing" "" "forge spec-review" "admin or maintain"
# 46 no sensitive configuration, no extra rule
LABELS="forge spec-review ready-for-dev" PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(planp infra/x.bicep)" 2026-11-18T08:30:00Z)]" FORGE_SENSITIVE_PATHS="" run
check "no config" "forge-coder-claude.yml" "forge ready-for-dev"
unset FORGE_SENSITIVE_PATHS

# ---- ADR-0024: Claude Code is the only engine. Every agent is dispatched as forge-<agent>-claude.yml with the inputs it
# takes, and a result never leads to anything else.
export FORGE_KIND=human P_EVENT=issues P_ACTION=labeled P_SENDER=alice P_LABEL=forge
LABELS="forge" run
check "ba is forge-ba-claude.yml" "forge-ba-claude.yml" "forge"
export FORGE_KIND=agent AGENT=ba CONCLUSION=success RUN_STARTED_AT="2026-11-18T09:00:00Z"
LABELS="forge" COMMENTS="[$(cm x "$(result ba complete)" 2026-11-18T10:00:00Z)]" run
check "planner is forge-planner-claude.yml" "forge-planner-claude.yml" "forge planning"
LABELS="forge" AGENT=reviewer PULLS='[{"number":7}]' PR_COMMENTS="[$(cm y "$(result reviewer changes-requested)" 2026-11-18T10:00:00Z)]" run
check "a replan takes the plan pull request and branch" "forge-planner-claude.yml.*pr=7.*ref=forge/issue-12" "forge planning"
LABELS="forge" AGENT=coder PULLS='[{"number":7}]' COMMENTS="[$(cm x "$(result planner ready-for-review 2 0)" 2026-11-18T08:30:00Z)]" PR_COMMENTS="[$(cm y "$(result coder ready-for-review)" 2026-11-18T10:00:00Z)]" FILES='[]' run
check "reviewer is forge-reviewer-claude.yml" "forge-reviewer-claude.yml.*thread=7" "forge"
# nothing but the Claude workflows is ever dispatched
if grep -q "\.lock\.yml" "$FAKE_DIR/dispatch.log"; then fail=$((fail+1)); echo "FAIL a lock file was dispatched"; else pass=$((pass+1)); fi
unset FORGE_KIND AGENT CONCLUSION

echo "passed=$pass failed=$fail"; [ "$fail" -eq 0 ]
