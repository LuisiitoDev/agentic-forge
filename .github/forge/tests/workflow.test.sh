#!/usr/bin/env bash
# Static checks on the intake workflow and the caller template (no GitHub). Guards what the spec requires of them.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
W="$ROOT/.github/workflows/forge-intake.yml"; C="$ROOT/.github/forge/templates/forge-caller.yml"
python3 -c "import yaml" 2>/dev/null || { echo "FAIL PyYAML is required for these checks"; exit 1; }
pass=0; fail=0
eq() { if [ "$2" = "$3" ]; then pass=$((pass+1)); else fail=$((fail+1)); echo "FAIL $1: expected [$3] got [$2]"; fi; }
py() { python3 -c "import sys,yaml,json; d=yaml.safe_load(open(sys.argv[1])); $2" "$1"; }

eq "intake parses"            "$(py "$W" 'print("ok")')" ok
eq "caller parses"            "$(py "$C" 'print("ok")')" ok
# PyYAML reads the key `on` as True
eq "intake is workflow_call"  "$(py "$W" 'print(list(d[True].keys()))')" "['workflow_call']"
eq "intake no permissions"    "$(py "$W" 'print(d["permissions"])')" "{}"
eq "caller no permissions"    "$(py "$C" 'print(d["permissions"])')" "{}"
eq "caller triggers"          "$(py "$C" 'print(sorted(d[True].keys()))')" "['issue_comment', 'issues']"
eq "caller has no steps"      "$(py "$C" 'print(any("steps" in j or "run" in j for j in d["jobs"].values()))')" False
eq "caller uses the intake"   "$(py "$C" 'print(d["jobs"]["forge"]["uses"].split("@")[0])')" "LuisiitoDev/agentic-forge/.github/workflows/forge-intake.yml"
eq "caller passes engine-ref" "$(py "$C" 'print("engine-ref" in d["jobs"]["forge"]["with"])')" True

# No expression inside a `run` block: event values go through env only (script injection).
eq "no \${{ }} in run" "$(py "$W" '
bad=[s["run"] for j in d["jobs"].values() for s in j["steps"] if "run" in s and "\${{" in s["run"]]
print(len(bad))')" 0
# Every third-party action is pinned to a full commit SHA.
eq "actions pinned" "$(py "$W" '
import re
print(all(re.search(r"@[0-9a-f]{40}( |$)", s["uses"]) for j in d["jobs"].values() for s in j["steps"] if "uses" in s))')" True
eq "checkout keeps no credentials" "$(py "$W" '
print([s["with"]["persist-credentials"] for j in d["jobs"].values() for s in j["steps"] if "uses" in s][0])')" False
# The allowlist file is fail-closed as shipped: no uncommented entry.
eq "targets.txt entries" "$(grep -cvE '^[[:space:]]*(#|$)' "$ROOT/.github/forge/targets.txt" || true)" 0

# The orchestrator (ADR-0018): human events only, and it waits for the agents it starts.
O="$ROOT/.github/workflows/forge-orchestrator.yml"
eq "orchestrator parses"       "$(py "$O" 'print("ok")')" ok
eq "orchestrator triggers"     "$(py "$O" 'print(sorted(d[True].keys()))')" "['repository_dispatch']"
eq "orchestrator waits"        "$(py "$O" 'print(d["jobs"]["route"]["steps"][-1]["env"]["FORGE_AWAIT"])')" 1
eq "orchestrator job outlives the wait" "$(py "$O" 'print(d["jobs"]["route"]["timeout-minutes"] * 60 > 3300)')" True
eq "orchestrator may dispatch" "$(py "$O" 'print(d["jobs"]["route"]["permissions"]["actions"])')" write
eq "orchestrator one per request" "$(py "$O" 'print(d["jobs"]["route"]["concurrency"]["cancel-in-progress"])')" False
eq "no workflow_run outside comments" "$(grep -v '^[[:space:]]*#' "$O" | grep -c 'workflow_run' || true)" 0

# The pre-flight workflow: started by hand, read-only, no event value inside a `run` block, actions pinned.
P="$ROOT/.github/workflows/forge-preflight.yml"
eq "preflight parses"        "$(py "$P" 'print("ok")')" ok
eq "preflight by hand only"  "$(py "$P" 'print(list(d[True].keys()))')" "['workflow_dispatch']"
eq "preflight reads only"    "$(py "$P" 'print(sorted(d["permissions"].items()))')" "[('actions', 'read'), ('contents', 'read')]"
eq "preflight no \${{ }} in run" "$(py "$P" '
print(len([s for j in d["jobs"].values() for s in j["steps"] if "run" in s and "\${{" in s["run"]]))')" 0
eq "preflight actions pinned" "$(py "$P" '
import re
print(all(re.search(r"@[0-9a-f]{40}( |$)", s["uses"]) for j in d["jobs"].values() for s in j["steps"] if "uses" in s))')" True
eq "preflight token step may fail" "$(py "$P" '
print([s.get("continue-on-error") for j in d["jobs"].values() for s in j["steps"] if s.get("id") == "app"][0])')" True

# The agents' GitHub tools are read-only (invariant 4): the compiled workflows run the GitHub server read-only and list
# no tool that writes. Checked on the lock files, which are what runs.
for f in "$ROOT"/.github/workflows/forge-{ba,planner,coder,infra,reviewer}.lock.yml; do
  n=$(basename "$f" .lock.yml)
  eq "$n github server read-only" "$(grep -c '"GITHUB_READ_ONLY": "1"' "$f" | awk '{print ($1 >= 1)}')" 1
  eq "$n lists no writing github tool" "$(python3 - "$f" <<'PY'
import sys,re,json
t=open(sys.argv[1]).read()
m=re.search(r'"mcp_servers":(\[.*?\}\])', t.splitlines()[1])
d=json.loads(m.group(1)) if m else []
bad=[x for s in d if s["name"]=="github" for x in s["tools"] if re.search(r"^(create|add|update|delete|merge|push|fork|assign|dismiss|enable|disable|resolve|unresolve|request|run|rerun|cancel|star|unstar)_|_(write|trigger)$", x)]
print(len(bad))
PY
)" 0
done
# The Reviewer reads CI results and security alerts; the App needs the matching read permissions (docs/github-app-setup.md).
eq "reviewer reads actions and alerts" "$(grep -c 'actions_list\|list_code_scanning_alerts\|list_dependabot_alerts' "$ROOT/.github/workflows/forge-reviewer.lock.yml" | awk '{print ($1 >= 1)}')" 1
eq "manifest grants the reviewer reads" "$(python3 -c "
import json
d=json.load(open('$ROOT/.github/forge/app-manifest.json'))['default_permissions']
print(all(d.get(k)=='read' for k in ['actions','security_events','vulnerability_alerts','secret_scanning_alerts']))")" True
eq "manifest grants nothing to write beyond the four" "$(python3 -c "
import json
d=json.load(open('$ROOT/.github/forge/app-manifest.json'))['default_permissions']
print(sorted(k for k,v in d.items() if v=='write'))")" "['contents', 'issues', 'pull_requests']"

echo "passed=$pass failed=$fail"; [ "$fail" -eq 0 ]
