#!/usr/bin/env bash
# Intake decision logic (docs/intake-spec.md, ADR-0015). Sourced by the intake workflow; testable without GitHub.
#
# It turns one issue event of a target into one repository_dispatch payload for the engine, or into nothing.
# It is an early filter, never the security boundary: the orchestrator re-checks the allowlist, the sender and
# the approval policy. It never reads issue titles, bodies or comment text, and never echoes its inputs.
#
# Inputs (environment), see the spec for their GitHub sources:
#   EVENT_NAME ACTION REPO SENDER SENDER_TYPE ISSUE IS_PR LABEL ISSUE_LABELS BOT_LOGIN ALLOWED_TARGETS DELIVERY
# intake_send also needs: ENGINE_REPO DISPATCH_TOKEN
#
#   intake_decide   prints one line: "dispatch <payload json>" or "ignore <reason>". Always returns 0.
#   intake_send     reads the payload on stdin and dispatches it to ENGINE_REPO. Non-zero on any failure.

set -euo pipefail
export LC_ALL=C

_lc() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }

intake_decide() {
  local ev="${EVENT_NAME:-}/${ACTION:-}"
  case "$ev" in
    issues/labeled | issue_comment/created) ;;
    *) echo "ignore event not handled"; return 0 ;;
  esac

  # Loop prevention: nothing the engine's own bot does may start a stage.
  local sender bot
  sender=$(_lc "${SENDER:-}"); bot=$(_lc "${BOT_LOGIN:-}")
  if [ "${SENDER_TYPE:-}" = Bot ] || [[ "$sender" == *'[bot]' ]] || { [ -n "$bot" ] && [ "$sender" = "$bot" ]; }; then
    echo "ignore sender is a bot"; return 0
  fi

  # Allowlist, whole-value and case-insensitive. An empty list fails closed.
  local entries=0 found=0 t repo
  repo=$(_lc "${REPO:-}")
  while IFS= read -r t; do
    t=$(printf '%s' "$t" | tr -d '[:space:]')
    [ -n "$t" ] || continue
    entries=$((entries + 1))
    [ "$(_lc "$t")" = "$repo" ] && found=1
  done < <(printf '%s\n' "${ALLOWED_TARGETS:-}" | tr ',' '\n')
  [ "$entries" -gt 0 ] || { echo "ignore allowlist is empty"; return 0; }
  [[ "${REPO:-}" =~ ^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$ ]] || { echo "ignore malformed target"; return 0; }
  [ "$found" -eq 1 ] || { echo "ignore target is not on the allowlist"; return 0; }

  [[ "${SENDER:-}" =~ ^[A-Za-z0-9-]+(\[bot\])?$ ]] || { echo "ignore malformed sender"; return 0; }
  [[ "${ISSUE:-}" =~ ^[1-9][0-9]*$ ]] || { echo "ignore malformed issue number"; return 0; }
  [ "${IS_PR:-}" != true ] || { echo "ignore pull request"; return 0; }

  local label=""
  if [ "$ev" = issues/labeled ]; then
    case "${LABEL:-}" in
      forge | ready-for-dev) label="$LABEL" ;;
      *) echo "ignore label not handled"; return 0 ;;
    esac
  else
    # A comment counts only on a request: the label `forge` itself, matched by whole line.
    grep -Fxq -- forge <<<"${ISSUE_LABELS:-}" || { echo "ignore comment is not on a request"; return 0; }
  fi

  # Exactly the contract of the dispatch payload; `label` only for label events.
  printf 'dispatch '
  jq -cn --arg event "$EVENT_NAME" --arg action "$ACTION" --arg target "$REPO" --argjson issue "$ISSUE" \
    --arg sender "$SENDER" --arg delivery "${DELIVERY:-}" --arg label "$label" \
    '{event:$event, action:$action, target:$target, issue:$issue, sender:$sender, delivery:$delivery}
     + (if $label == "" then {} else {label:$label} end)'
}

intake_send() {
  [ -n "${DISPATCH_TOKEN:-}" ] || { echo "no dispatch token" >&2; return 1; }
  jq -c '{event_type: "forge", client_payload: .}' |
    GH_TOKEN="$DISPATCH_TOKEN" gh api -X POST "repos/$ENGINE_REPO/dispatches" --input - >/dev/null
}
