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

# 10 FORGE_BA_ENGINE=claude: the orchestrator dispatches forge-ba-claude.yml and waits for the run of that workflow,
# found by its own title (forge-ba-claude o/r#12), then follows to the planner as usual
cchain() { runs_of ba-claude "[$(runjson 99 'forge-ba o/r#12' 2026-11-18T09:00:06Z), $(runjson 103 'forge-ba-claude o/r#12' 2026-11-18T09:00:05Z)]"; ok_run 103 2026-11-18T09:00:08Z
  runs_of planner "[$(runjson 104 "$(title planner)" 2026-11-18T09:06:00Z)]"; ok_run 104 2026-11-18T09:06:03Z; }
LABELS="forge" FORGE_BA_ENGINE=claude SETUP=cchain PULLS='[{"number":7}]' \
  COMMENTS="[$(cm x "$(result ba complete)" 2026-11-18T09:05:00Z), $(cm y "$(result planner ready-for-review)" 2026-11-18T09:10:00Z)]" run
check "claude ba, then planner" "forge-planner" "forge spec-review"
eq_ba=$(grep -c 'forge-ba-claude.yml' "$FAKE_DIR/dispatch.log"); if [ "$eq_ba" = 1 ]; then pass=$((pass+1)); else fail=$((fail+1)); echo "FAIL claude ba dispatched $eq_ba times"; fi

# 11 a run of the copilot workflow with the same title is not ours when the claude twin was dispatched
wrongwf() { runs_of ba "[$(runjson 105 "$(title ba)" 2026-11-18T09:00:05Z)]"; ok_run 105 2026-11-18T09:00:08Z; }
LABELS="forge" FORGE_BA_ENGINE=claude SETUP=wrongwf run
check "claude ba ignores the copilot workflow" "" "blocked forge" "did not start"
unset FORGE_BA_ENGINE

echo "passed=$pass failed=$fail"; [ "$fail" -eq 0 ]
