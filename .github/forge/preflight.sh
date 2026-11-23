#!/usr/bin/env bash
# Pre-flight check for the Forge engine and one target (docs/first-run-checklist.md). It only reads: it changes
# nothing and prints no secret. One table row per check: PASS, WARN, FAIL or INFO. Exit code 1 if any check failed.
#
# Environment (set by .github/workflows/forge-preflight.yml):
#   TARGET            owner/name of the target to check
#   ENGINE_REPO       this repository, owner/name
#   ENGINE_TOKEN      the workflow's own token
#   TARGET_TOKEN      the App installation token for TARGET; empty if it could not be created
#   APP_SLUG          slug of the App, from the token step; may be empty
#   ACTOR             who started the check (used to try the collaborator-permission lookup)
#   ALLOWED           the value of FORGE_ALLOWED_TARGETS
#   BOT_LOGIN         the value of FORGE_BOT_LOGIN
#   APP_ID            the value of FORGE_APP_ID
#   HAS_APP_KEY, HAS_ANTHROPIC   "1" when the secrets FORGE_APP_KEY, ANTHROPIC_API_KEY exist, else empty
#   MODEL             the value of FORGE_MODEL; empty means the default compiled into the agent workflows
#   TARGETS_FILE      path of the engine's .github/forge/targets.txt (default: the one next to this script)

set -uo pipefail
export LC_ALL=C

FAILS=0; WARNS=0
row() { printf '| %s | %s | %s |\n' "$1" "$2" "$3"; case "$1" in FAIL) FAILS=$((FAILS + 1)) ;; WARN) WARNS=$((WARNS + 1)) ;; esac; }
tgt() { GH_TOKEN="$TARGET_TOKEN" gh "$@"; }
eng() { GH_TOKEN="$ENGINE_TOKEN" gh "$@"; }
lc() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
# in_list <needle> <list>: whole entry, case-insensitive; entries separated by commas or newlines, `#` comments.
in_list() { printf '%s\n' "$2" | sed 's/#.*//' | tr ',' '\n' | tr -d '[:blank:]' | grep -Fixq -- "$1"; }
# file_of <path>: the decoded content of a file of the target, empty if it does not exist.
file_of() { tgt api "repos/$TARGET/contents/$1" --jq .content 2>/dev/null | base64 -d 2>/dev/null; }

summary() {
  echo
  echo "**$FAILS failed, $WARNS warnings.**"
  [ "$FAILS" -eq 0 ]
}

echo "## Forge pre-flight for \`${TARGET:-?}\`"
echo
echo "| Result | Check | Detail |"
echo "| --- | --- | --- |"

if ! [[ "${TARGET:-}" =~ ^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$ ]]; then
  row FAIL "target" "\`TARGET\` must be owner/name"; summary; exit 1
fi
: "${ENGINE_REPO:?}" "${TARGETS_FILE:=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/targets.txt}"

# 1. Engine configuration. Secrets are only reported as present or missing.
[ -n "${APP_ID:-}" ] && row PASS "FORGE_APP_ID" "set" || row FAIL "FORGE_APP_ID" "variable is empty"
[ -n "${HAS_APP_KEY:-}" ] && row PASS "FORGE_APP_KEY" "secret exists" || row FAIL "FORGE_APP_KEY" "secret is missing"
[ -n "${HAS_ANTHROPIC:-}" ] && row PASS "ANTHROPIC_API_KEY" "secret exists" || row FAIL "ANTHROPIC_API_KEY" "secret is missing: no agent can start (ADR-0020)"
[ -n "${BOT_LOGIN:-}" ] && row PASS "FORGE_BOT_LOGIN" "\`$BOT_LOGIN\`" || row FAIL "FORGE_BOT_LOGIN" "variable is empty: no agent result would be trusted"

# 2. The two allowlists must both name the target.
if in_list "$TARGET" "${ALLOWED:-}"; then row PASS "FORGE_ALLOWED_TARGETS" "lists the target"
else row FAIL "FORGE_ALLOWED_TARGETS" "does not list \`$TARGET\`: the orchestrator would skip every event"; fi
if in_list "$TARGET" "$(cat "$TARGETS_FILE" 2>/dev/null)"; then row PASS "targets.txt" "lists the target (on the branch this check runs on)"
else row FAIL "targets.txt" "does not list \`$TARGET\`: the intake would ignore every event"; fi

# 3. The App.
if [ -z "${TARGET_TOKEN:-}" ]; then
  row FAIL "App token" "could not be created for \`$TARGET\`: the App is not installed on it, \`FORGE_APP_ID\` or \`FORGE_APP_KEY\` is wrong, or the target is not spelled as installed. Target checks below are skipped"
else
  row PASS "App token" "created for the target only"
  if [ -z "${APP_SLUG:-}" ]; then row WARN "bot login" "the App slug is unknown, so \`FORGE_BOT_LOGIN\` was not compared"
  elif [ "$(lc "${BOT_LOGIN:-}")" = "$(lc "$APP_SLUG[bot]")" ]; then row PASS "bot login" "\`FORGE_BOT_LOGIN\` matches the App"
  else row FAIL "bot login" "\`FORGE_BOT_LOGIN\` is \`${BOT_LOGIN:-}\` but the App is \`$APP_SLUG[bot]\`: its results would not be trusted"; fi

  if tgt api "repos/$TARGET" --jq .full_name >/dev/null 2>&1; then row PASS "target repository" "readable with the App token"
  else row FAIL "target repository" "not readable with the App token"; fi

  if [[ "${ACTOR:-}" =~ ^[A-Za-z0-9-]+$ ]] && perm=$(tgt api "repos/$TARGET/collaborators/$ACTOR/permission" --jq .permission 2>/dev/null) && [ -n "$perm" ]; then
    row PASS "permission lookup" "\`$ACTOR\` has \`$perm\` on the target: the lookup the orchestrator uses for approvals works"
  else row FAIL "permission lookup" "the App token cannot read collaborator permissions of the target"; fi

  labels=$(tgt api "repos/$TARGET/labels?per_page=100" --paginate --jq '.[].name' 2>/dev/null) || labels=""
  missing=""
  for l in forge ready-for-dev; do grep -Fxq -- "$l" <<<"$labels" || missing="$missing \`$l\`"; done
  if [ -z "$missing" ]; then row PASS "labels" "\`forge\` and \`ready-for-dev\` exist"
  else row WARN "labels" "missing:$missing. Create them: a person cannot add a label that does not exist"; fi

  cfg=$(file_of .forge/config.yml)
  if [ -z "$cfg" ]; then row FAIL ".forge/config.yml" "missing in the target: the scope gate allows nothing without it"
  else
    paths=$(printf '%s\n' "$cfg" | yq '((.paths.app // []) + (.paths.infra // []))[]' 2>/dev/null)
    if [ -z "$paths" ]; then row FAIL ".forge/config.yml" "declares no \`paths.app\` or \`paths.infra\`: the scope gate allows nothing"
    else
      filelike=""
      while IFS= read -r p; do
        base="${p%/}"; base="${base##*/}"
        if [[ "$base" == *.* && "$p" != */ ]]; then filelike="$filelike \`$p\`"; fi
      done <<<"$paths"
      if [ -z "$filelike" ]; then row PASS ".forge/config.yml" "declares allowed paths"
      else row WARN ".forge/config.yml" "paths are directory prefixes; these look like files and would not match:$filelike"; fi
    fi
  fi

  caller=$(file_of .github/workflows/forge.yml)
  if [ -z "$caller" ]; then row FAIL "caller workflow" "\`.github/workflows/forge.yml\` is missing in the target"
  else
    read -r cuses cref <<<"$(printf '%s\n' "$caller" | sed -n 's/^[[:space:]]*uses:[[:space:]]*\([^@[:space:]]*\)@\([^[:space:]#]*\).*/\1 \2/p' | head -1)"
    eref=$(printf '%s\n' "$caller" | sed -n 's/^[[:space:]]*engine-ref:[[:space:]]*\([^[:space:]#]*\).*/\1/p' | head -1)
    if [ -z "${cref:-}" ] || [ -z "$eref" ]; then row FAIL "caller workflow" "no \`uses: ...@ref\` or no \`engine-ref\` found"
    elif [[ "$cref" == *"<"* || "$eref" == *"<"* ]]; then row FAIL "caller workflow" "still has the \`<TAG-OR-SHA>\` placeholder"
    elif [ "$cref" != "$eref" ]; then row FAIL "caller workflow" "\`uses\` is pinned to \`$cref\` but \`engine-ref\` is \`$eref\`: they must be the same"
    elif [ "$(lc "${cuses%%/.github/*}")" != "$(lc "$ENGINE_REPO")" ]; then row FAIL "caller workflow" "calls \`${cuses%%/.github/*}\`, not this engine (\`$ENGINE_REPO\`)"
    elif [[ "$cref" =~ ^[0-9a-f]{40}$ ]]; then row PASS "caller workflow" "pinned to a full commit SHA, both places equal"
    else row WARN "caller workflow" "pinned to \`$cref\`, which is not a full commit SHA: a tag can move"; fi

    # The intake reads targets.txt at the pinned ref, so that ref must already name the target.
    if [[ "${cref:-}" =~ ^[A-Za-z0-9._/-]+$ ]] && [[ "$cref" != *"<"* ]]; then
      pinned=$(eng api "repos/$ENGINE_REPO/contents/.github/forge/targets.txt?ref=$cref" --jq .content 2>/dev/null | base64 -d 2>/dev/null) || pinned=""
      if [ -z "$pinned" ]; then row FAIL "pinned ref" "\`targets.txt\` cannot be read at \`$cref\` in the engine"
      elif in_list "$TARGET" "$pinned"; then row PASS "pinned ref" "\`targets.txt\` at \`$cref\` lists the target"
      else row FAIL "pinned ref" "\`targets.txt\` at \`$cref\` does not list \`$TARGET\`: the intake would ignore every event. Pin a newer commit"; fi
    fi
  fi
fi

# 4. The engine's workflows are registered and active.
active=$(eng api "repos/$ENGINE_REPO/actions/workflows?per_page=100" --jq '.workflows[] | select(.state == "active") | .name' 2>/dev/null) || active=""
missing=""
for w in forge-ba forge-planner forge-coder forge-infra forge-reviewer forge-orchestrator forge-intake; do grep -Fxq -- "$w" <<<"$active" || missing="$missing \`$w\`"; done
if [ -z "$missing" ]; then row PASS "engine workflows" "all seven are active"
else row FAIL "engine workflows" "not active on the default branch:$missing"; fi

# 5. What cannot be checked from here.
row INFO "dispatch token" "lives in the target as \`FORGE_DISPATCH_TOKEN\`; the first intake run is its check"
if [ -n "${MODEL:-}" ]; then model="\`$MODEL\` from \`FORGE_MODEL\`"
else model="the default compiled into the agent workflows (\`FORGE_MODEL\` is not set)"; fi
row INFO "model" "$model; whether the API key can use it shows on the first agent run"

summary
