#!/usr/bin/env bash
# Tests of the orchestrator's wait for the agent runs it dispatches (ADR-0018), with a fake `gh`.
# The orchestration chain: a human event dispatches an agent, the orchestrator waits for that run, reads its
# result and dispatches the next one, until a human gate or a failure ends it.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
FAKE_DIR="$(mktemp -d)"; trap 'rm -rf "$FAKE_DIR"' EXIT
export PATH="$ROOT/.github/forge/tests/bin:$PATH" FAKE_DIR
pass=0; fail=0
cm() { jq -n --arg b "$2" --arg t "$3" --arg u "${4:-forge[bot]}" '{body:$b, created_at:$t, user:{login:$u}}'; }
result() { local t; case "$1" in ba) t=BA ;; *) t="${1^}" ;; esac
  printf '## %s result: %s\n\n```yaml\nstatus: %s\ntasks:\n  app: %s\n  infra: %s\n```\n' "$t" "$2" "$2" "${3:-1}" "${4:-0}"; }
# runs_of <agent> <json array of runs>: the runs the fake lists for forge-<agent>
runs_of() { printf '{"workflow_runs":%s}\n' "$2" > "$FAKE_DIR/runs-forge-$1.json"; }
# run_info <id> <status> <conclusion> <started_at>
run_info() { jq -n --arg s "$2" --arg c "$3" --arg t "$4" '{status:$s, conclusion:(if $c=="" then null else $c end), run_started_at:$t}' > "$FAKE_DIR/run-$1.json"; }
title() { printf 'forge-%s o/r#12' "$1"; }
runjson() { jq -cn --argjson id "$1" --arg t "$2" --arg c "$3" '{id:$id, display_title:$t, created_at:$c, event:"workflow_dispatch"}'; }

run() { # SETUP names a function that writes the fake runs; everything else comes from the environment
  rm -rf "$FAKE_DIR"; mkdir -p "$FAKE_DIR"
  printf '%s\n' $LABELS > "$FAKE_DIR/labels.txt" 2>/dev/null || true
  echo "${COMMENTS:-[]}" > "$FAKE_DIR/comments.json"; echo "${PULLS:-[]}" > "$FAKE_DIR/pulls.json"
  touch "$FAKE_DIR/dispatch.log" "$FAKE_DIR/posted.txt"
  [ -n "${SETUP:-}" ] && "$SETUP"
  ( export TARGET=o/r ISSUE=12 TARGET_TOKEN=t ENGINE_TOKEN=e ENGINE_REPO=eng/forge ENGINE_REF=main FORGE_BOT_LOGIN="forge[bot]" RUN_URL=http://run
    source "$ROOT/.github/forge/orchestrate.sh"; main ) >/dev/null 2>"$FAKE_DIR/err.log"
}
check() { # name expect_dispatch_names(space-sep, in order) expect_labels(sorted) [expect_posted_substring]
  local d l p ok=1; d=$(grep -o 'forge-[a-z]*\.lock\.yml' "$FAKE_DIR/dispatch.log" | sed 's/\.lock\.yml//' | xargs)
  l=$(sort "$FAKE_DIR/labels.txt" | xargs); p=$(cat "$FAKE_DIR/posted.txt")
  [ "$d" != "$2" ] && ok=0
  [ "$l" != "$3" ] && ok=0
  [ -n "${4:-}" ] && ! grep -q -- "$4" <<<"$p" && ok=0
  [ -f "$FAKE_DIR/unhandled.log" ] && { ok=0; cat "$FAKE_DIR/unhandled.log"; }
  if [ $ok = 1 ]; then pass=$((pass+1)); else fail=$((fail+1)); echo "FAIL $1"; echo "  dispatched: [$d]"; echo "  labels: [$l]"; echo "  posted: $p"; echo "  stderr: $(tail -3 "$FAKE_DIR/err.log" | tr '\n' ' ')"; fi
}
# A human event that starts a request, with the wait on and no real sleeping.
export FORGE_KIND=human P_EVENT=issues P_ACTION=labeled P_SENDER=alice P_LABEL=forge
export FORGE_AWAIT=1 AWAIT_INTERVAL=0 AWAIT_FIND_TRIES=2 AWAIT_MAX=1000 FORGE_NOW="2026-11-18T08:59:50Z"
unset FORGE_MAX_STAGES

BA_RUNS() { runs_of ba "[$(runjson 99 'forge-ba o/x#12' 2026-11-18T09:00:06Z), $(runjson 101 "$(title ba)" 2026-11-18T09:00:05Z)]"; }
ok_run() { run_info "$1" completed success "$2"; }

# 1 BA complete, then the planner: the chain follows both runs and stops at the human gate
chain() { BA_RUNS; ok_run 101 2026-11-18T09:00:08Z
  runs_of planner "[$(runjson 102 "$(title planner)" 2026-11-18T09:06:00Z)]"; ok_run 102 2026-11-18T09:06:03Z; }
LABELS="forge" SETUP=chain PULLS='[{"number":7}]' \
  COMMENTS="[$(cm x "$(result ba complete)" 2026-11-18T09:05:00Z), $(cm y "$(result planner ready-for-review)" 2026-11-18T09:10:00Z)]" run
check "chain ba, planner, gate" "forge-ba forge-planner" "forge spec-review"

# 2 BA asks questions: the chain ends at the human gate, nothing else is dispatched
chain2() { BA_RUNS; ok_run 101 2026-11-18T09:00:08Z; }
LABELS="forge" SETUP=chain2 COMMENTS="[$(cm x "$(result ba needs-clarification)" 2026-11-18T09:05:00Z)]" run
check "ba asks, chain ends" "forge-ba" "forge needs-clarification"

# 3 the run never appears
none() { :; }
LABELS="forge" SETUP=none run
check "run never appears" "forge-ba" "blocked forge" "did not start"

# 4 only a run of another request, or an older run of this one, is visible: not taken as ours
decoy() { runs_of ba "[$(runjson 99 'forge-ba o/x#12' 2026-11-18T09:00:06Z), $(runjson 98 "$(title ba)" 2026-11-18T08:00:00Z)]"; ok_run 98 2026-11-18T08:00:01Z; ok_run 99 2026-11-18T09:00:07Z; }
LABELS="forge" SETUP=decoy COMMENTS="[$(cm x "$(result ba complete)" 2026-11-18T09:05:00Z)]" run
check "decoy and stale runs ignored" "forge-ba" "blocked forge" "did not start"

# 5 the run ends in failure
failed() { BA_RUNS; run_info 101 completed failure 2026-11-18T09:00:08Z; }
LABELS="forge" SETUP=failed run
check "run failed" "forge-ba" "blocked forge" "ended with"

# 6 the run does not finish in time
slow() { BA_RUNS; run_info 101 in_progress "" 2026-11-18T09:00:08Z; }
LABELS="forge" SETUP=slow AWAIT_MAX=0 run
check "run too slow" "forge-ba" "blocked forge" "did not finish"

# 6b the token the orchestrator holds is about to expire: stop waiting while it can still say so
LABELS="forge" SETUP=slow FORGE_TOKEN_BUDGET=0 run
check "token budget" "forge-ba" "blocked forge" "token"

# 7 the run ends fine but left no result of its own after it started
stale() { BA_RUNS; ok_run 101 2026-11-18T09:00:08Z; }
LABELS="forge" SETUP=stale COMMENTS="[$(cm x "$(result ba complete)" 2026-11-18T08:00:00Z)]" run
check "stale result" "forge-ba" "blocked forge" "no result"

# 8 a limit on the stages of one orchestration
LABELS="forge" SETUP=chain PULLS='[{"number":7}]' FORGE_MAX_STAGES=1 \
  COMMENTS="[$(cm x "$(result ba complete)" 2026-11-18T09:05:00Z), $(cm y "$(result planner ready-for-review)" 2026-11-18T09:10:00Z)]" run
check "stage limit" "forge-ba forge-planner" "blocked forge" "too many stages"

# 8b every dispatch carries the target repository's name alone: the agents scope their App token with it
LABELS="forge" SETUP=chain PULLS='[{"number":7}]' \
  COMMENTS="[$(cm x "$(result ba complete)" 2026-11-18T09:05:00Z), $(cm y "$(result planner ready-for-review)" 2026-11-18T09:10:00Z)]" run
n=$(grep -c -- "-f target_repo_name=r" "$FAKE_DIR/dispatch.log"); [ "$n" = 2 ] && pass=$((pass+1)) || { fail=$((fail+1)); echo "FAIL dispatch passes target_repo_name ($n of 2)"; cat "$FAKE_DIR/dispatch.log"; }

# 9 without FORGE_AWAIT nothing waits: the first dispatch is the whole orchestration (the unit tests of the stages)
LABELS="forge" SETUP=chain FORGE_AWAIT=0 run
check "no wait when off" "forge-ba" "forge"

# ---------------------------------------------------------------- rate limits (ADR-0020)
# A failed run whose failed-step logs show a provider 429 is dispatched again with the same inputs; any other failure
# is not. No real waiting: the base of the backoff is 0.
export FORGE_RETRY_BASE=0
LOG_429='agent	Execute Claude Code CLI	API Error: 429 {"type":"error","error":{"type":"rate_limit_error","message":"Number of request tokens has exceeded your per-minute rate limit"}}'
LOG_OTHER='agent	Execute Claude Code CLI	Error: tool call failed: permission denied'
LOG_BUDGET='agent	Execute Claude Code CLI	429 Maximum LLM invocations exceeded (20 / 20)'
NEEDS_CLARIF="[$(cm x "$(result ba needs-clarification)" 2026-11-18T09:20:00Z)]"
# rate_limited_then <n> <log>: the first n BA runs fail with <log>; the run after them succeeds
rate_limited_then() { local i id
  for ((i = 0; i <= $1; i++)); do
    id=$((101 + i))
    runs_of ba "[$(runjson "$id" "$(title ba)" "2026-11-18T09:0${i}:05Z")]"
    mv "$FAKE_DIR/runs-forge-ba.json" "$FAKE_DIR/runs-forge-ba.$((i + 1)).json"
    if [ "$i" -lt "$1" ]; then run_info "$id" completed failure "2026-11-18T09:0${i}:08Z"; printf '%s\n' "$2" > "$FAKE_DIR/runlog-$id.txt"
    else ok_run "$id" "2026-11-18T09:0${i}:08Z"; fi
  done; }
limited_once() { rate_limited_then 1 "$LOG_429"; }
limited_always() { rate_limited_then 9 "$LOG_429"; }
other_failure() { rate_limited_then 1 "$LOG_OTHER"; }
budget_spent() { rate_limited_then 1 "$LOG_BUDGET"; }
no_log() { rate_limited_then 1 "$LOG_429"; rm -f "$FAKE_DIR"/runlog-*.txt; }

# 10 a 429, then a good run: the stage is retried once and the chain carries on; the retry is written on the request
LABELS="forge" SETUP=limited_once COMMENTS="$NEEDS_CLARIF" run
check "429 is retried" "forge-ba forge-ba" "forge needs-clarification" "attempt 2 of 4"
n=$(grep -c 'Rate limited' "$FAKE_DIR/posted.txt"); [ "$n" = 1 ] && pass=$((pass+1)) || { fail=$((fail+1)); echo "FAIL one audit comment per retry ($n)"; }
grep -q 'actions/runs/101' "$FAKE_DIR/posted.txt" && pass=$((pass+1)) || { fail=$((fail+1)); echo "FAIL audit comment links the rate limited run"; }
# the retry carries the same inputs as the first dispatch
d1=$(sed -n 1p "$FAKE_DIR/dispatch.log"); d2=$(sed -n 2p "$FAKE_DIR/dispatch.log")
[ -n "$d1" ] && [ "$d1" = "$d2" ] && pass=$((pass+1)) || { fail=$((fail+1)); echo "FAIL retry has the same inputs"; echo "  $d1"; echo "  $d2"; }

# 11 any other failure is not retried
LABELS="forge" SETUP=other_failure COMMENTS="$NEEDS_CLARIF" run
check "other failure not retried" "forge-ba" "blocked forge" "ended with"
! grep -q 'Rate limited' "$FAKE_DIR/posted.txt" && pass=$((pass+1)) || { fail=$((fail+1)); echo "FAIL other failure wrote a retry comment"; }

# 12 the 429 of a spent max-turns budget is not a rate limit: waiting does not fix it
LABELS="forge" SETUP=budget_spent COMMENTS="$NEEDS_CLARIF" run
check "budget 429 not retried" "forge-ba" "blocked forge" "ended with"

# 13 logs that cannot be read: fail closed, no retry
LABELS="forge" SETUP=no_log COMMENTS="$NEEDS_CLARIF" run
check "unreadable log not retried" "forge-ba" "blocked forge" "ended with"

# 14 the attempt limit: 4 runs by default, then the request is blocked
LABELS="forge" SETUP=limited_always COMMENTS="$NEEDS_CLARIF" run
check "attempt limit default" "forge-ba forge-ba forge-ba forge-ba" "blocked forge" "all 4 attempts"
LABELS="forge" SETUP=limited_always COMMENTS="$NEEDS_CLARIF" FORGE_RATE_LIMIT_ATTEMPTS=2 run
check "attempt limit configurable" "forge-ba forge-ba" "blocked forge" "all 2 attempts"
LABELS="forge" SETUP=limited_always COMMENTS="$NEEDS_CLARIF" FORGE_RATE_LIMIT_ATTEMPTS=nonsense run
check "attempt limit not a number" "forge-ba forge-ba forge-ba forge-ba" "blocked forge" "all 4 attempts"

# 15 a retry is not a new stage: one stage allowed, a 429 and a retry still fit
LABELS="forge" SETUP=limited_once COMMENTS="$NEEDS_CLARIF" FORGE_MAX_STAGES=1 run
check "retry is not a stage" "forge-ba forge-ba" "forge needs-clarification"

# 16 no retry when the wait would outlive the orchestrator's token
LABELS="forge" SETUP=limited_once COMMENTS="$NEEDS_CLARIF" FORGE_RETRY_BASE=60 FORGE_TOKEN_BUDGET=20 run
check "no retry past the token budget" "forge-ba" "blocked forge" "no time is left"

# 17 the backoff: doubles per attempt, capped, with jitter between half and all of it
( source "$ROOT/.github/forge/orchestrate.sh"
  bad=0
  for _ in $(seq 50); do
    d1=$(FORGE_RETRY_BASE=10 retry_delay 1); d3=$(FORGE_RETRY_BASE=10 retry_delay 3); dc=$(FORGE_RETRY_BASE=10 FORGE_RETRY_CAP=25 retry_delay 6)
    { [ "$d1" -ge 5 ] && [ "$d1" -le 10 ] && [ "$d3" -ge 20 ] && [ "$d3" -le 40 ] && [ "$dc" -ge 13 ] && [ "$dc" -le 25 ]; } || bad=1
  done
  seen=$(for _ in $(seq 50); do FORGE_RETRY_BASE=100 retry_delay 1; done | sort -u | wc -l)
  [ "$bad" = 0 ] && [ "$seen" -gt 1 ] ) && pass=$((pass+1)) || { fail=$((fail+1)); echo "FAIL backoff bounds and jitter"; }
unset FORGE_RETRY_BASE

echo "passed=$pass failed=$fail"; [ "$fail" -eq 0 ]
