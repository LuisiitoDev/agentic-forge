#!/usr/bin/env bash
# Tests for the intake decision logic (docs/intake-spec.md). No network, no GitHub: `gh` is the fake in tests/bin.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
FAKE_DIR="$(mktemp -d)"; trap 'rm -rf "$FAKE_DIR"' EXIT
export PATH="$ROOT/.github/forge/tests/bin:$PATH" FAKE_DIR
SCRIPT="${INTAKE_SCRIPT:-$ROOT/.github/forge/intake.sh}"
pass=0; fail=0
eq() { if [ "$2" = "$3" ]; then pass=$((pass+1)); else fail=$((fail+1)); echo "FAIL $1: expected [$3] got [$2]"; fi; }
[ -f "$SCRIPT" ] || { echo "FAIL missing $SCRIPT"; exit 1; }

# dec VAR=value ...  -> the one line intake_decide prints, with defaults overridden. Values are never eval'd.
dec() (
  export EVENT_NAME=issues ACTION=labeled REPO=me/app SENDER=alice SENDER_TYPE=User ISSUE=7 IS_PR=false
  export LABEL=forge ISSUE_LABELS="" BOT_LOGIN="my-forge[bot]" ALLOWED_TARGETS="me/app" DELIVERY=run-1-1
  for kv in "$@"; do export "$kv"; done
  source "$SCRIPT"
  intake_decide
)
kind() { printf '%s' "${1%% *}"; }
payload() { printf '%s' "${1#dispatch }" | jq -cS .; }
is_ignored() { eq "$1" "$(kind "$(dec "${@:2}")")" ignore; }
is_dispatched() { eq "$1" "$(kind "$(dec "${@:2}")")" dispatch; }

# I-01, I-02
is_dispatched "I-01 label forge" LABEL=forge
is_dispatched "I-02 label ready-for-dev" LABEL=ready-for-dev
# I-03
for l in bug forge:claude not-forge "forge " Forge ""; do is_ignored "I-03 label [$l]" "LABEL=$l"; done
# I-04
is_ignored "I-04 opened"     ACTION=opened
is_ignored "I-04 unlabeled"  ACTION=unlabeled
is_ignored "I-04 edited"     EVENT_NAME=issue_comment ACTION=edited ISSUE_LABELS=forge
# I-05, I-06, I-07
r=$(dec EVENT_NAME=issue_comment ACTION=created LABEL= ISSUE_LABELS=$'bug\nforge')
eq "I-05 comment dispatch" "$(kind "$r")" dispatch
eq "I-05 no label field"   "$(payload "$r" | jq 'has("label")')" false
is_ignored "I-06 no forge label"   EVENT_NAME=issue_comment ACTION=created ISSUE_LABELS=bug
is_ignored "I-06 only forge:claude" EVENT_NAME=issue_comment ACTION=created ISSUE_LABELS=forge:claude
is_ignored "I-06 substring"        EVENT_NAME=issue_comment ACTION=created ISSUE_LABELS=not-forge
is_ignored "I-07 pull request"     EVENT_NAME=issue_comment ACTION=created ISSUE_LABELS=forge IS_PR=true
is_ignored "I-07 pr on label event" IS_PR=true
# I-08
is_ignored "I-08 type Bot"        SENDER_TYPE=Bot
is_ignored "I-08 [bot] suffix"    SENDER="someone[bot]"
is_ignored "I-08 BOT_LOGIN case"  SENDER="MY-FORGE[bot]" SENDER_TYPE=User
is_ignored "I-08 BOT_LOGIN plain" SENDER=forgebot BOT_LOGIN=forgebot
# I-09, I-10
is_ignored "I-09 off allowlist" REPO=evil/repo
is_ignored "I-09 prefix only"   REPO=me/app-extra
is_ignored "I-09 suffix only"   REPO=me/ap
is_ignored "I-09 empty list"    ALLOWED_TARGETS=
is_ignored "I-09 blank list"    ALLOWED_TARGETS=$' \n , '
is_dispatched "I-10 messy list" REPO=ME/APP "ALLOWED_TARGETS= other/x ,
 me/app
,"
# I-11
for t in app me/app/extra "me/a pp" /app me/ "me/app;x"; do is_ignored "I-11 repo [$t]" "REPO=$t" "ALLOWED_TARGETS=$t"; done
# I-12
for n in abc 0 007 -1 "" "1 2" "7.5"; do is_ignored "I-12 issue [$n]" "ISSUE=$n"; done
# I-13, I-17
marker="$(mktemp -u)"
for s in "a b" 'a"b' 'x$(touch '"$marker"')' 'x`touch '"$marker"'`' $'a\nb' "a;b" ""; do
  out=$(dec "SENDER=$s")
  eq "I-13 sender [$s] ignored" "$(kind "$out")" ignore
  [ -e "$marker" ] && { fail=$((fail+1)); echo "FAIL I-13 command executed"; rm -f "$marker"; }
  case "$out" in *'$('*|*'`'*|*touch*|*"a b"*) fail=$((fail+1)); echo "FAIL I-17 reason echoes input: $out" ;; *) pass=$((pass+1)) ;; esac
done
# I-14
is_ignored "I-14 push"   EVENT_NAME=push
is_ignored "I-14 empty"  EVENT_NAME=
# I-15
r=$(dec LABEL=forge DELIVERY=run-9-2)
eq "I-15 payload issues" "$(payload "$r")" '{"action":"labeled","delivery":"run-9-2","event":"issues","issue":7,"label":"forge","sender":"alice","target":"me/app"}'
eq "I-15 issue is number" "$(payload "$r" | jq -r '.issue|type')" number
r=$(dec EVENT_NAME=issue_comment ACTION=created ISSUE_LABELS=forge SENDER=bob "REPO=Me/App")
eq "I-15 payload comment" "$(payload "$r")" '{"action":"created","delivery":"run-1-1","event":"issue_comment","issue":7,"sender":"bob","target":"Me/App"}'
# I-16: single line, starts with ignore, exit 0
for args in "ACTION=opened" "SENDER_TYPE=Bot" "ALLOWED_TARGETS=" "ISSUE=x" $'SENDER=a\nb'; do
  out=$(dec "$args"); rc=$?
  eq "I-16 rc [$args]" "$rc" 0
  eq "I-16 one line [$args]" "$(printf '%s\n' "$out" | wc -l | tr -d ' ')" 1
  case "$out" in "ignore "*) pass=$((pass+1)) ;; *) fail=$((fail+1)); echo "FAIL I-16 format [$args]: $out" ;; esac
done
# determinism
eq "deterministic" "$(dec)" "$(dec)"

# I-18, I-19, I-20: intake_send
send() ( # extra env as VAR=value
  rm -rf "$FAKE_DIR"; mkdir -p "$FAKE_DIR"
  export ENGINE_REPO=eng/forge DISPATCH_TOKEN=dispatch-tok GH_TOKEN=ambient-tok
  for kv in "$@"; do export "$kv"; done
  source "$SCRIPT"
  printf '%s' '{"event":"issues","action":"labeled","target":"me/app","issue":7,"sender":"alice","delivery":"run-1-1","label":"forge"}' | intake_send >/dev/null 2>&1
)
send; rc=$?
eq "I-18 success rc" "$rc" 0
eq "I-18 event_type" "$(jq -r .event_type "$FAKE_DIR/dispatch_event.json")" forge
eq "I-18 payload" "$(jq -cS .client_payload "$FAKE_DIR/dispatch_event.json")" '{"action":"labeled","delivery":"run-1-1","event":"issues","issue":7,"label":"forge","sender":"alice","target":"me/app"}'
eq "I-20 token" "$(sort -u "$FAKE_DIR/tokens.log" | xargs)" dispatch-tok
send DISPATCH_FAIL=1; rc=$?
eq "I-19 failure is non-zero" "$([ "$rc" -ne 0 ] && echo yes || echo no)" yes
# a token-less send must not fall back to the ambient token
send DISPATCH_TOKEN=; rc=$?
eq "I-20 no token fails" "$([ "$rc" -ne 0 ] && echo yes || echo no)" yes

echo "passed=$pass failed=$fail"; [ "$fail" -eq 0 ]
