#!/usr/bin/env bash
# Delivery job of a Claude agent: validates the result and the changes, publishes them, posts the result.
# The agent never writes to GitHub; this is the only place that does, with a token scoped to the target.
# Env: AGENT TARGET ISSUE THREAD PR REF GH_TOKEN (write token, also used by the git checkout in target/)
#      ART (dir with result.md, changes.patch, names.txt)   ENGINE_DIR (this repository's checkout)
set -euo pipefail
art="${ART:-artifacts}"; engine="${ENGINE_DIR:-engine}"
case "$AGENT" in ba|planner|coder|infra|reviewer) ;; *) echo "unknown agent" >&2; exit 1 ;; esac
[[ "$TARGET" =~ ^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$ && "$ISSUE" =~ ^[0-9]+$ && "$THREAD" =~ ^[0-9]+$ ]] || { echo "bad inputs" >&2; exit 1; }
[[ -z "${PR:-}" || "${PR}" =~ ^[0-9]+$ ]] || { echo "bad PR" >&2; exit 1; }
title() { case "$1" in ba) echo BA ;; *) printf '%s' "${1^}" ;; esac; }
T=$(title "$AGENT")
log() { printf '[deliver] %s\n' "$*" >&2; }

result="$art/result.md"
head -n1 "$result" | grep -Eq "^## $T result: [a-z-]+\$" || { echo "::error::The result does not start with '## $T result: <status>'"; exit 1; }
[ "$(wc -c < "$result")" -le 60000 ] || { echo "::error::The result is too long"; exit 1; }
perl -0pi -e 's/<!--.*?-->//gs' "$result"   # HTML comments are removed when published
status=$(awk '/^```yaml/ {f=1; next} /^```/ {if (f) exit} f' "$result" | awk '/^status:/ {sub(/^status:[[:space:]]*/, ""); sub(/[[:space:]]+#.*$/, ""); gsub(/^"|"$/, ""); print; exit}')
log "$AGENT status: ${status:-unknown}"

reject() { # replaces the agent's result by a blocked one that says why nothing was published
  { printf '## %s result: blocked\n\n```yaml\nstatus: blocked\n```\n\n' "$T"
    printf 'The engine did not publish the changes of the %s agent:\n\n%s\n' "$AGENT" "$(printf '%s\n' "$1" | sed 's/^/- /')"
  } > "$result"
  status=blocked
}

writer=0; case "$AGENT" in planner|coder|infra) writer=1 ;; esac
publish=0
if [ "$writer" = 1 ] && [ -s "$art/changes.patch" ] && [ "$status" != blocked ]; then
  default=$(gh repo view "$TARGET" --json defaultBranchRef --jq .defaultBranchRef.name)
  allowed=""
  case "$AGENT" in
    coder | infra)
      key=app; [ "$AGENT" = infra ] && key=infra
      cfg=$(git -C target show "origin/$default:.forge/config.yml" 2>/dev/null || true)
      # -r: raw strings, whichever yq the runner has (the Go one and the jq wrapper both accept it); quotes stripped anyway.
      allowed=$(printf '%s\n' "$cfg" | yq -r ".paths.$key // [] | .[]" 2>/dev/null | sed -E 's/^"//; s/"$//' || true) ;;
  esac
  if reasons=$(ALLOWED="$allowed" bash "$engine/.github/forge/claude/validate-changes.sh" "$AGENT" "$ISSUE" "$art/changes.patch" < "$art/names.txt"); then
    publish=1
  else
    log "changes rejected"; reject "$reasons"
  fi
fi

if [ "$publish" = 1 ]; then
  git -C target config user.name "forge-agent"
  git -C target config user.email "forge-agent@users.noreply.github.com"
  msg="[forge] $AGENT changes for #$ISSUE"
  if [ "$AGENT" = planner ] && [ -z "${PR:-}" ]; then
    branch="forge/issue-$ISSUE"
    existing=$(gh pr list --repo "$TARGET" --head "$branch" --state open --json number --jq '.[0].number // empty')
    if [ -n "$existing" ]; then
      log "plan pull request #$existing already exists: updating its branch (idempotent rerun)"
      git -C target fetch -q origin "$branch"; git -C target checkout -q -B "$branch" "origin/$branch"
    else
      git -C target checkout -q -B "$branch" "origin/$default"
    fi
    git -C target apply --index --whitespace=nowarn -p2 "../$art/changes.patch"
    git -C target commit -q -m "$msg"
    git -C target push -q origin "HEAD:refs/heads/$branch"
    if [ -z "$existing" ]; then
      rtitle=$(gh issue view "$ISSUE" --repo "$TARGET" --json title --jq .title | tr -d '\r\n' | cut -c1-200)
      gh pr create --repo "$TARGET" --draft --base "$default" --head "$branch" --title "[forge] $rtitle" \
        --body "Plan for #$ISSUE, prepared by the Forge planner. Closes #$ISSUE"
    fi
  else
    [ -n "${PR:-}" ] || { echo "::error::A pull request is required to publish $AGENT changes"; exit 1; }
    info=$(gh pr view "$PR" --repo "$TARGET" --json headRefName,title,state,isCrossRepository)
    [ "$(jq -r .headRefName <<<"$info")" = "$REF" ] || { echo "::error::The pull request head is not $REF"; exit 1; }
    [ "$(jq -r .state <<<"$info")" = OPEN ] || { echo "::error::The pull request is not open"; exit 1; }
    [ "$(jq -r .isCrossRepository <<<"$info")" = false ] || { echo "::error::The pull request comes from a fork"; exit 1; }
    case "$(jq -r .title <<<"$info")" in "[forge] "*) ;; *) echo "::error::The pull request title does not start with [forge]"; exit 1 ;; esac
    git -C target fetch -q origin "$REF"; git -C target checkout -q -B "$REF" "origin/$REF"
    git -C target apply --index --whitespace=nowarn -p2 "../$art/changes.patch"
    git -C target commit -q -m "$msg"
    git -C target push -q origin "HEAD:refs/heads/$REF"
  fi
fi

gh issue comment "$THREAD" --repo "$TARGET" --body-file "$result"
