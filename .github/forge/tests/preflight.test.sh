#!/usr/bin/env bash
# Tests of the pre-flight check (docs/first-run-checklist.md), with a fake `gh`. It reads configuration and prints a
# table; it changes nothing.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
FAKE_DIR="$(mktemp -d)"; trap 'rm -rf "$FAKE_DIR"' EXIT
export PATH="$ROOT/.github/forge/tests/bin:$PATH" FAKE_DIR
pass=0; fail=0
SHA=0123456789abcdef0123456789abcdef01234567
b64() { printf '%s' "$1" | base64 -w0; }
file_json() { jq -cn --arg c "$(b64 "$2")" '{content:$c}' > "$FAKE_DIR/$1"; }
caller() { printf 'name: forge\non:\n  issues:\n    types: [labeled]\njobs:\n  forge:\n    uses: eng/forge/.github/workflows/forge-intake.yml@%s\n    with:\n      engine-ref: %s\n    secrets:\n      dispatch-token: ${{ secrets.FORGE_DISPATCH_TOKEN }}\n' "$1" "${2:-$1}"; }
good_wf='{"workflows":[{"name":"forge-ba-claude","state":"active"},{"name":"forge-planner-claude","state":"active"},{"name":"forge-coder-claude","state":"active"},{"name":"forge-reviewer-claude","state":"active"},{"name":"forge-orchestrator","state":"active"},{"name":"forge-intake","state":"active"}]}'

setup_good() {
  rm -rf "$FAKE_DIR"; mkdir -p "$FAKE_DIR/contents/.forge" "$FAKE_DIR/contents/.github/workflows"
  : > "$FAKE_DIR/dispatch.log"
  printf 'forge\nready-for-dev\nbug\n' > "$FAKE_DIR/repo-labels.txt"
  file_json contents/.forge/config.yml $'paths:\n  app: [docs/]\n'
  file_json contents/.github/workflows/forge.yml "$(caller $SHA)"
  file_json engine-targets.json $'# targets\no/r\n'
  echo "$good_wf" > "$FAKE_DIR/workflows.json"
}
pf() { # prints the table; env overrides the good defaults
  ( export TARGET=o/r ENGINE_REPO=eng/forge ENGINE_TOKEN=e TARGET_TOKEN=t ACTOR=alice APP_SLUG=forge ALLOWED="o/r" \
      BOT_LOGIN="forge[bot]" APP_ID=123 HAS_APP_KEY=1 HAS_CLAUDE=1 TARGETS_FILE="$FAKE_DIR/targets.txt"
    printf '# the allowlist\no/r\n' > "$TARGETS_FILE"
    for kv in "$@"; do export "$kv"; done
    bash "$ROOT/.github/forge/preflight.sh" ) 2>&1
}
expect() { # name, table, status-of-row-regex...
  local name="$1" out="$2" want="$3" ok=1
  grep -qE -- "$want" <<<"$out" || ok=0
  [ -f "$FAKE_DIR/unhandled.log" ] && { ok=0; cat "$FAKE_DIR/unhandled.log"; }
  if [ $ok = 1 ]; then pass=$((pass+1)); else fail=$((fail+1)); echo "FAIL $name"; echo "  wanted: $want"; echo "$out" | sed 's/^/  | /' | head -30; fi
}
exitcode() { ( "$@" >/dev/null 2>&1; echo $? ); }

# 1 everything in order: no FAIL
setup_good; out=$(pf); expect "all good" "$out" "PASS"; ! grep -q "FAIL" <<<"$out" && pass=$((pass+1)) || { fail=$((fail+1)); echo "FAIL all good has a FAIL"; echo "$out" | head -30; }
setup_good; pf >/dev/null; rc=$?; [ $rc -eq 0 ] && pass=$((pass+1)) || { fail=$((fail+1)); echo "FAIL all good exit code $rc"; }

# 2 engine configuration missing
setup_good; expect "no app key"      "$(pf HAS_APP_KEY=)" "FAIL.*FORGE_APP_KEY"
setup_good; expect "no claude token"  "$(pf HAS_CLAUDE=)" "FAIL.*CLAUDE_CODE_OAUTH_TOKEN"
setup_good; expect "claude token ok"  "$(pf)" "PASS.*CLAUDE_CODE_OAUTH_TOKEN"
setup_good; rc=$(exitcode pf HAS_CLAUDE=); [ "$rc" != 0 ] && pass=$((pass+1)) || { fail=$((fail+1)); echo "FAIL missing Claude token must fail the preflight (exit code $rc)"; }
setup_good; expect "engine row"       "$(pf)" "INFO.*Claude Code runs every agent"
setup_good; ! grep -qi "copilot" <<<"$(pf)" && pass=$((pass+1)) || { fail=$((fail+1)); echo "FAIL the preflight still mentions Copilot"; }
setup_good; expect "no app id"       "$(pf APP_ID=)" "FAIL.*FORGE_APP_ID"
setup_good; expect "no bot login"    "$(pf BOT_LOGIN=)" "FAIL.*FORGE_BOT_LOGIN"
# 3 allowlists
setup_good; expect "not in variable" "$(pf ALLOWED=other/x)" "FAIL.*FORGE_ALLOWED_TARGETS"
setup_good; printf '' > /dev/null; expect "not in targets.txt" "$(pf TARGETS_FILE=/dev/null)" "FAIL.*targets.txt"
setup_good; expect "case insensitive" "$(pf ALLOWED=O/R)" "PASS.*FORGE_ALLOWED_TARGETS"
# 4 the App token
setup_good; expect "no token"        "$(pf TARGET_TOKEN=)" "FAIL.*App token"
setup_good; expect "bot login differs" "$(pf APP_SLUG=other)" "FAIL.*bot login"
setup_good; expect "target unreadable" "$(pf REPO_FAIL=1)" "FAIL.*target repository"
setup_good; expect "permission lookup" "$(pf PERM=write)" "PASS.*permission lookup"
# 5 labels, config
setup_good; printf 'bug\n' > "$FAKE_DIR/repo-labels.txt"; expect "labels missing" "$(pf)" "WARN.*labels"
setup_good; rm "$FAKE_DIR/contents/.forge/config.yml"; expect "no config" "$(pf)" "FAIL.*config.yml"
setup_good; file_json contents/.forge/config.yml $'paths:\n  app: []\n'; expect "empty paths" "$(pf)" "FAIL.*config.yml"
setup_good; file_json contents/.forge/config.yml $'paths:\n  app: [README.md]\n'; expect "file as path" "$(pf)" "WARN.*config.yml"
setup_good; file_json contents/.forge/config.yml $'paths:\n  app: [src]\n'; expect "no trailing slash is fine" "$(pf)" "PASS.*config.yml"
# 6 the caller
setup_good; rm "$FAKE_DIR/contents/.github/workflows/forge.yml"; expect "no caller" "$(pf)" "FAIL.*caller"
setup_good; file_json contents/.github/workflows/forge.yml "$(caller '<TAG-OR-SHA>')"; expect "placeholder pin" "$(pf)" "FAIL.*caller"
setup_good; file_json contents/.github/workflows/forge.yml "$(caller $SHA main)"; expect "pins differ" "$(pf)" "FAIL.*caller"
setup_good; file_json contents/.github/workflows/forge.yml "$(caller v1)"; expect "tag pin warns" "$(pf)" "WARN.*caller"
# 7 the pinned ref must already allow the target
setup_good; file_json engine-targets.json $'# none\n'; expect "ref does not allow target" "$(pf)" "FAIL.*pinned"
# 8 workflows of the engine
setup_good; echo '{"workflows":[{"name":"forge-ba-claude","state":"active"}]}' > "$FAKE_DIR/workflows.json"; expect "workflows missing" "$(pf)" "FAIL.*engine workflows"
# 9 nothing is changed and nothing secret is printed
setup_good; out=$(pf); [ ! -s "$FAKE_DIR/dispatch.log" ] && ! grep -qE "tokens.log" <<<"$out" && ! grep -q -- "-X \(POST\|PUT\|PATCH\|DELETE\)" "$FAKE_DIR/calls.log" && pass=$((pass+1)) || { fail=$((fail+1)); echo "FAIL read-only"; grep -- "-X" "$FAKE_DIR/calls.log"; }

echo "passed=$pass failed=$fail"; [ "$fail" -eq 0 ]
