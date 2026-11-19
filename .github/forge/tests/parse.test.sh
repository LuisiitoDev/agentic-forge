#!/usr/bin/env bash
# Unit tests for how the orchestrator reads agent results. No network, no GitHub.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
source "$ROOT/.github/forge/orchestrate.sh"
pass=0; fail=0
eq() { if [ "$2" = "$3" ]; then pass=$((pass+1)); else fail=$((fail+1)); echo "FAIL $1: expected [$3] got [$2]"; fi; }

planner_body=$'<!-- planner:result -->\n## Planner result: ready-for-review\n\n```yaml\nstatus: ready-for-review   # ready-for-review | needs-clarification | blocked\ntype: feature              # feature | bug\nplan_dir: .forge/requests/12/\ntasks:\n  app: 3\n  infra: 1\npaths:\n  - src/a.cs\n```\n\n### Summary\nstatus: not-this-one\n'
res=$(jq -n --arg b "$planner_body" '{body:$b, created_at:"2026-11-18T10:00:00Z"}')
eq "status"        "$(result_status "$res")" "ready-for-review"
eq "tasks.app"     "$(result_tasks "$res" app)" "3"
eq "tasks.infra"   "$(result_tasks "$res" infra)" "1"
eq "created"       "$(result_created "$res")" "2026-11-18T10:00:00Z"

# value with quotes, no comment; status text appearing later in the body must not win
ba_body=$'<!-- ba:result -->\n```yaml\nstatus: "complete"\ntype: bug\n```\nstatus: blocked\n'
res2=$(jq -n --arg b "$ba_body" '{body:$b, created_at:"x"}')
eq "quoted status" "$(result_status "$res2")" "complete"

# no yaml block => empty status (handled as unknown by the caller)
res3=$(jq -n --arg b $'<!-- ba:result -->\nno block here\nstatus: complete' '{body:$b, created_at:"x"}')
eq "no block" "$(result_status "$res3")" ""

# tasks section absent
res4=$(jq -n --arg b $'```yaml\nstatus: ready-for-review\n```' '{body:$b, created_at:"x"}')
eq "no tasks" "$(result_tasks "$res4" app)" ""

# nested key must not leak from a different parent
res5=$(jq -n --arg b $'```yaml\nstatus: x\nother:\n  app: 9\ntasks:\n  infra: 2\n```' '{body:$b, created_at:"x"}')
eq "scoped parent" "$(result_tasks "$res5" app)" ""
eq "scoped infra"  "$(result_tasks "$res5" infra)" "2"

# freshness (string compare of ISO timestamps)
RUN_STARTED_AT="2026-11-18T09:59:00Z"; fresh "$res"  && eq "fresh newer" ok ok || eq "fresh newer" bad ok
RUN_STARTED_AT="2026-11-18T10:00:00Z"; fresh "$res"  && eq "fresh equal" ok ok || eq "fresh equal" bad ok
RUN_STARTED_AT="2026-11-18T10:00:01Z"; fresh "$res"  && eq "stale" bad ok || eq "stale" ok ok


# yaml_list: block lists, quotes, comments, and the end of the list
yl() { printf '%s\n' "$1" | yaml_block | yaml_list paths | paste -sd'|' -; }
eq "list basic"   "$(yl $'```yaml\nstatus: x\npaths:\n  - a/b.cs\n  - c/d\n```')" "a/b.cs|c/d"
eq "list quotes"  "$(yl $'```yaml\npaths:\n  - "a b/c"\n  - \'d/e\'\n```')" "a b/c|d/e"
eq "list comment" "$(yl $'```yaml\npaths:\n  - a/b  # why\n```')" "a/b"
eq "list ends at next key" "$(yl $'```yaml\npaths:\n  - a\ntasks:\n  - not-a-path\n```')" "a"
eq "list absent"  "$(yl $'```yaml\nstatus: x\n```')" ""
eq "flow list unread" "$(yl $'```yaml\npaths: [a, b]\n```')" ""
echo "passed=$pass failed=$fail"; [ "$fail" -eq 0 ]
