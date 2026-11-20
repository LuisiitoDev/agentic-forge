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

echo "passed=$pass failed=$fail"; [ "$fail" -eq 0 ]
