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

# The agents run as plain workflows (ADR-0022, ADR-0024): their GitHub access is what the App tokens they mint allow.
CW="$ROOT/.github/workflows/forge-agent-claude.yml"; CA="$ROOT/.github/actions/claude-awf/action.yml"
# Every App token an agent workflow mints is scoped to the target repository by name (the default is the engine repo,
# which made the checkout of the target fail with a 404 on the first real run).
eq "every App token is scoped to the target repository" "$(py "$CW" '
steps=[s for j in d["jobs"].values() for s in j["steps"] if "create-github-app-token" in s.get("uses","")]
print(len(steps), all(s["with"].get("repositories")=="\x24{{ inputs.target_repo_name }}" for s in steps))')" "2 True"
# No agent workflow asks the App for the `administration` permission: the App has no access to repository settings.
eq "no administration permission" "$(grep -c 'permission-administration' "$CW" || true)" 0
# The agent job reads (contents, issues, pull requests; the Reviewer also CI runs and alerts); only deliver can write.
eq "the Reviewer's read token asks for CI and alerts, the others' does not" "$(py "$CW" '
w=[s["with"] for s in d["jobs"]["agent"]["steps"] if "create-github-app-token" in s.get("uses","")][0]
print(all(("reviewer" in str(w[k])) for k in ("permission-actions","permission-security-events","permission-vulnerability-alerts")))')" True
eq "manifest grants the reviewer reads" "$(python3 -c "
import json
d=json.load(open('$ROOT/.github/forge/app-manifest.json'))['default_permissions']
print(all(d.get(k)=='read' for k in ['actions','security_events','vulnerability_alerts','secret_scanning_alerts']))")" True
eq "manifest grants nothing to write beyond the four" "$(python3 -c "
import json
d=json.load(open('$ROOT/.github/forge/app-manifest.json'))['default_permissions']
print(sorted(k for k,v in d.items() if v=='write'))")" "['contents', 'issues', 'pull_requests']"

# No relative link in a Markdown file points at a file that does not exist (a deleted document leaves none behind).
eq "no broken relative link in the Markdown files" "$(cd "$ROOT" && python3 -c "
import re,os,subprocess
bad=0
for f in subprocess.check_output(['git','ls-files','*.md'],text=True).split():
    base=os.path.dirname(f)
    for m in re.finditer(r'\]\(([^)#\s]+)(#[^)]*)?\)',open(f).read()):
        t=m.group(1)
        if t.startswith(('http','mailto:')): continue
        if not os.path.exists(os.path.normpath(os.path.join(base,t))): bad+=1
print(bad)")" 0
# ADR-0024: no `gh aw` (Copilot) layer is left: no source or lock file, no actions lock, no generated-file attributes.
eq "no gh aw workflow, lock or actions lock" "$(ls "$ROOT"/.github/workflows/*.lock.yml "$ROOT"/.github/aw 2>/dev/null | wc -l)" 0
eq "the agent workflows are the Claude ones only" "$(ls "$ROOT"/.github/workflows/forge-{ba,planner,coder,reviewer}.md 2>/dev/null | wc -l)" 0
eq "no Copilot in the engine scripts" "$(grep -il 'copilot' "$ROOT"/.github/forge/*.sh "$ROOT"/.github/forge/claude/*.py "$ROOT"/.github/workflows/*.yml "$ROOT"/.github/actions/*/* 2>/dev/null | wc -l)" 0

# Claude twins (ADR-0022): the thin workflows, the reusable one and the composite action.
for a in ba planner coder reviewer; do
  W2="$ROOT/.github/workflows/forge-$a-claude.yml"
  eq "$a twin calls the reusable workflow with its own agent" "$(py "$W2" 'j=d["jobs"]["run"]; print(j["uses"], j["with"]["agent"])')" "./.github/workflows/forge-agent-claude.yml $a"
  eq "$a twin takes the inputs the orchestrator passes" "$(py "$W2" 'print(sorted(d[True]["workflow_dispatch"]["inputs"]))')" "['issue', 'pr', 'ref', 'target', 'target_repo_name', 'thread']"
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
# The preflight passes the secrets to its script by name: a typo here would make it report a secret as missing.
PW="$ROOT/.github/workflows/forge-preflight.yml"
eq "preflight reads the Claude token secret" "$(grep -c "HAS_CLAUDE: \${{ secrets.CLAUDE_CODE_OAUTH_TOKEN != '' && '1' || '' }}" "$PW")" 1
eq "preflight does not read a Copilot secret" "$(grep -c "COPILOT" "$PW" || true)" 0
# ADR-0023: the Infra agent is gone, in every shape.
eq "no infra agent spec, workflow or lock" "$(ls "$ROOT"/.github/forge/agents/infra.md "$ROOT"/.github/workflows/forge-infra* 2>/dev/null | wc -l)" 0
eq "the orchestrator never dispatches infra" "$(grep -c 'dispatch forge-infra' "$ROOT/.github/forge/orchestrate.py")" 0
eq "no FORGE_INFRA variable in the orchestrator workflow" "$(grep -c 'FORGE_INFRA' "$ROOT/.github/workflows/forge-orchestrator.yml")" 0
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
