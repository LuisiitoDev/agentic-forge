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
bad=[s["run"] for j in d["jobs"].values() for s in j["steps"] if "run" in s and "\x24{{" in s["run"]]
print(len(bad))')" 0
# Every third-party action is pinned to a full commit SHA.
eq "actions pinned" "$(py "$W" '
import re
print(all(re.search(r"@[0-9a-f]{40}( |$)", s["uses"]) for j in d["jobs"].values() for s in j["steps"] if "uses" in s))')" True
eq "checkout keeps no credentials" "$(py "$W" '
print([s["with"]["persist-credentials"] for j in d["jobs"].values() for s in j["steps"] if "uses" in s][0])')" False
# The allowlist file holds only exact owner/name entries: no wildcard, no organization alone, no spaces. Empty is
# allowed and means nothing is forwarded (fail closed).
eq "targets.txt entries are owner/name" "$(grep -vE '^[[:space:]]*(#|$)' "$ROOT/.github/forge/targets.txt" | grep -cvE '^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$' || true)" 0

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
print(len([s for j in d["jobs"].values() for s in j["steps"] if "run" in s and "\x24{{" in s["run"]]))')" 0
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
# Every App token an agent workflow mints is scoped to the target repository by name (the default is the engine repo,
# which made the checkout of the target fail with a 404 on the first real run).
for f in "$ROOT"/.github/workflows/forge-{ba,planner,coder,infra,reviewer}.lock.yml; do
  n=$(basename "$f" .lock.yml)
  eq "$n tokens scoped to the target" "$(py "$f" '
steps=[s for j in d["jobs"].values() for s in j.get("steps",[]) if "create-github-app-token" in s.get("uses","")]
print(len(steps) > 0 and all(s["with"].get("repositories") == "\x24{{ inputs.target_repo_name }}" for s in steps))')" True
  eq "$n has the target_repo_name input" "$(py "$f" 'print("target_repo_name" in d[True]["workflow_dispatch"]["inputs"])')" True
  eq "$n opens no failure issue" "$(grep -c 'GH_AW_FAILURE_REPORT_AS_ISSUE: "false"\|FAILURE_REPORT_AS_ISSUE: false' "$f" | awk '{print ($1 >= 1)}')" 1
done
# No agent workflow asks the App for the `administration` permission. `gh aw` adds it for the branch-protection pre-flight
# of a push (`check-branch-protection`), which is switched off: GitHub still enforces protection at push time, and the App
# does not have, and should not need, access to repository settings (first real run, 2026-11-23).
for f in "$ROOT"/.github/workflows/forge-{ba,planner,coder,infra,reviewer}.lock.yml; do
  eq "$(basename "$f" .lock.yml) does not ask for administration" "$(grep -c 'permission-administration' "$f" || true)" 0
done
# The engine's bot is a trusted author for the agents' GitHub tools. Without it `gh aw` filters the bot's own comments
# (the BA result the Planner must read) because an App bot is below `approved` (first real run, 2026-11-23).
for f in "$ROOT"/.github/workflows/forge-{ba,planner,coder,infra,reviewer}.lock.yml; do
  eq "$(basename "$f" .lock.yml) trusts the engine bot" "$(grep -c 'GH_AW_TRUSTED_USERS_EXTRA: \${{ vars.FORGE_BOT_LOGIN }}' "$f" | awk '{print ($1 >= 1)}')" 1
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

# Copilot engine (ADR-0017): every agent pins gpt-4.1 in the source and in the compiled lock. `auto` resolves to a
# model the account cannot use, and no fallback model is configured (a 429 is never answered by switching models).
for a in ba planner coder infra reviewer; do
  src="$ROOT/.github/workflows/forge-$a.md"; lock="$ROOT/.github/workflows/forge-$a.lock.yml"
  eq "$a engine"        "$(python3 -c "
import sys,yaml
print(yaml.safe_load(open(sys.argv[1]).read().split('---')[1])['engine'])" "$src")" "{'id': 'copilot', 'model': 'gpt-4.1'}"
  eq "$a lock model"    "$(grep -c 'COPILOT_MODEL: gpt-4.1' "$lock")" 2
  eq "$a lock no auto"  "$(grep -c 'COPILOT_MODEL: auto' "$lock" || true)" 0
done

# Claude twins (ADR-0022): the thin workflows, the reusable one and the composite action.
CW="$ROOT/.github/workflows/forge-agent-claude.yml"; CA="$ROOT/.github/actions/claude-awf/action.yml"
for a in ba planner coder infra reviewer; do
  W2="$ROOT/.github/workflows/forge-$a-claude.yml"
  eq "$a twin calls the reusable workflow with its own agent" "$(py "$W2" 'j=d["jobs"]["run"]; print(j["uses"], j["with"]["agent"])')" "./.github/workflows/forge-agent-claude.yml $a"
  eq "$a twin takes the inputs of the gh aw workflow" "$(py "$W2" 'print(sorted(d[True]["workflow_dispatch"]["inputs"]))')" "['issue', 'pr', 'ref', 'target', 'target_repo_name', 'thread']"
  eq "$a twin run-name matches what the orchestrator waits for" "$(py "$W2" 'print(d["run-name"])')" "forge-$a-claude \${{ inputs.target }}#\${{ inputs.issue }}"
  eq "$a twin grants only contents: read" "$(py "$W2" 'print(d["permissions"])')" "{'contents': 'read'}"
done
eq "reusable workflow grants nothing by default"  "$(py "$CW" 'print(d["permissions"])')" "{}"
eq "the agent job cannot write to GitHub"         "$(py "$CW" 'print(d["jobs"]["agent"]["permissions"])')" "{'contents': 'read'}"
eq "the agent job never gets a write token"       "$(py "$CW" 'a=d["jobs"]["agent"]["steps"]; print(any("write" in str(s.get("with",{})) for s in a))')" False
eq "the agent job does not keep credentials"      "$(py "$CW" 'a=d["jobs"]["agent"]["steps"]; print([s["with"]["persist-credentials"] for s in a if "checkout" in s.get("uses","")])')" "[False, False]"
eq "only the deliver job writes"                  "$(py "$CW" 'a=d["jobs"]["deliver"]["steps"]; print([s["with"].get("permission-contents") for s in a if "create-github-app-token" in s.get("uses","")])')" "['write']"
# GitHub parses workflow YAML more strictly than PyYAML: a flow mapping ({ a: b, c: d }) must not hold free text with
# commas, which the first Claude workflow did. No flow mapping or flow sequence at all in the workflows we write.
eq "no inline mappings in the Claude workflows" "$(grep -nE '^[[:space:]]*[A-Za-z_-]+:[[:space:]]*\{[^}]*:' "$CW" "$CA" "$ROOT"/.github/workflows/forge-*-claude.yml | grep -v 'fromJSON\|\${{' | wc -l)" 0
eq "every workflow_call input is described, typed and quoted" "$(py "$CW" '
i=d[True]["workflow_call"]["inputs"]
print(all(isinstance(v.get("description"),str) and v.get("type")=="string" and "required" in v for v in i.values()))')" True
# Every script the Claude workflows run exists (a rename must not leave a workflow pointing at nothing).
eq "scripts referenced by the Claude workflows exist" "$(grep -ho 'engine/\.github/forge/claude/[A-Za-z_]*\.py' "$CW" | sort -u | while read -r f; do [ -f "$ROOT/${f#engine/}" ] || echo "$f"; done | wc -l)" 0
eq "the Claude workflows reference the scripts" "$(grep -ho 'engine/\.github/forge/claude/[A-Za-z_]*\.py' "$CW" | sort -u | wc -l)" 5
eq "no shell script is left in forge/claude" "$(ls "$ROOT"/.github/forge/claude/*.sh 2>/dev/null | wc -l)" 0
# No expression inside a run block (script injection), in the reusable workflow and in the composite action.
eq "no \${{ }} in run (reusable)" "$(py "$CW" '
print(len([s for j in d["jobs"].values() for s in j["steps"] if "run" in s and "\x24{{" in s["run"]]))')" 0
eq "no \${{ }} in run (action)" "$(py "$CA" 'print(len([s for s in d["runs"]["steps"] if "run" in s and "\x24{{" in s["run"]]))')" 0
eq "third-party actions are pinned to a SHA" "$(python3 -c "
import yaml,re,sys
bad=[]
for f in sys.argv[1:]:
    d=yaml.safe_load(open(f))
    steps=[s for j in d.get('jobs',{}).values() for s in j.get('steps',[])] + d.get('runs',{}).get('steps',[])
    for s in steps:
        u=s.get('uses')
        if u and not u.startswith('./') and not re.search(r'@[0-9a-f]{40}\$', u): bad.append(u)
print(len(bad))" "$CW" "$CA")" 0
eq "the oauth token is not passed on a command line" "$(cat "$CA" "$ROOT/.github/actions/claude-awf/run_claude.py" | grep -c -- '--env CLAUDE_CODE_OAUTH_TOKEN\|CLAUDE_CODE_OAUTH_TOKEN=\$')" 0
eq "the action runs a Python script, not inline shell" "$(py "$CA" 'print([s["run"] for s in d["runs"]["steps"] if "run_claude" in s.get("run","")])')" "['python3 \"\$ACTION_PATH/run_claude.py\"']"
eq "the action script exists" "$([ -f "$ROOT/.github/actions/claude-awf/run_claude.py" ] && echo yes || echo no)" yes

echo "passed=$pass failed=$fail"; [ "$fail" -eq 0 ]
