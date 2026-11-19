#!/usr/bin/env bash
# Agentic Forge orchestrator. Deterministic routing: no model is involved here.
#
# Two kinds of events reach this script (see .github/workflows/forge-orchestrator.yml):
#   human  - a repository_dispatch from the relay: a human labeled or commented on a request issue
#   agent  - a forge-* agent workflow finished: read its result and decide the next stage
#
# Authority rules (ADR-0008): only a human with write access may start or approve. Events caused by bots never
# do. Agents never name labels; this script maps their `status` to labels and to the next stage.
#
# Required environment (set by the workflow):
#   FORGE_KIND            human | agent
#   TARGET, ISSUE         the request: owner/name and issue number (already validated)
#   TARGET_TOKEN          GitHub App installation token scoped to TARGET only
#   ENGINE_TOKEN          the workflow's own token, used only to dispatch agent workflows
#   ENGINE_REPO, ENGINE_REF   this repository and its default branch
#   FORGE_BOT_LOGIN       login of the App's bot; only its comments are trusted results
#   RUN_URL               link to this orchestrator run, for comments
# Optional policy (engine configuration, never read from the target):
#   FORGE_ALLOW_SELF_APPROVAL   "true" lets the person who opened a request approve its plan. Default: false
#   FORGE_SENSITIVE_PATHS       path prefixes, comma or newline separated. A plan that touches one needs an
#                               approver with the admin or maintain role. Default: none
# human:  P_EVENT, P_ACTION, P_SENDER, P_LABEL
# agent:  AGENT (ba|planner|coder|infra|reviewer), CONCLUSION, RUN_STARTED_AT

set -euo pipefail
export LC_ALL=C

MARK='<!-- forge:orchestrator -->'
STATE_LABELS=(needs-clarification planning spec-review needs-review blocked)

log() { printf '[forge] %s\n' "$*" >&2; }

# ---------------------------------------------------------------- GitHub access
tgt() { GH_TOKEN="$TARGET_TOKEN" gh "$@"; }
eng() { GH_TOKEN="$ENGINE_TOKEN" gh "$@"; }

issue_labels() { tgt api "repos/$TARGET/issues/$ISSUE/labels" --paginate --jq '.[].name'; }
# Read the labels first: `grep -q` closing the pipe early would make `pipefail` report a false negative.
has_label() {
  local labels
  labels=$(issue_labels)
  grep -Fxq -- "$1" <<<"$labels"
}
add_label() { tgt api -X POST "repos/$TARGET/issues/$ISSUE/labels" -f "labels[]=$1" >/dev/null; }
remove_label() {
  local enc
  enc=$(jq -rn --arg l "$1" '$l|@uri')
  tgt api -X DELETE "repos/$TARGET/issues/$ISSUE/labels/$enc" >/dev/null 2>&1 || true
}
comment() { tgt api -X POST "repos/$TARGET/issues/$ISSUE/comments" -f body="$MARK
$1" >/dev/null; }

ensure_labels() {
  local l
  for l in forge ready-for-dev "${STATE_LABELS[@]}"; do
    tgt api -X POST "repos/$TARGET/labels" -f name="$l" -f color="5319e7" >/dev/null 2>&1 || true
  done
}

# Exactly one state label at a time.
set_state() {
  local l
  for l in "${STATE_LABELS[@]}"; do
    [ "$l" = "$1" ] || remove_label "$l"
  done
  add_label "$1"
}

clear_state() {
  local l
  for l in "${STATE_LABELS[@]}"; do remove_label "$l"; done
}

# ---------------------------------------------------------------- results written by agents
# Prints the latest trusted result comment of an agent as JSON, or nothing.
# Trusted = authored by the engine's bot and starting with the agent's marker.
# Results live on the request issue (BA, first plan) and on the plan pull request (everything after), so both
# are read. A pull request's conversation comments are served by the issues API under its own number.
latest_result() {
  local pr
  pr=$(plan_pr)
  {
    tgt api "repos/$TARGET/issues/$ISSUE/comments" --paginate --jq '.[]'
    if [ -n "$pr" ]; then tgt api "repos/$TARGET/issues/$pr/comments" --paginate --jq '.[]'; fi
  } |
    jq -s --arg bot "$FORGE_BOT_LOGIN" --arg m "<!-- $1:result -->" '
      [ .[] | select(.user.login == $bot and (.body | startswith($m))) ]
      | sort_by(.created_at) | last // empty'
}

# First ```yaml fence of a comment body, read from stdin.
yaml_block() { awk '/^```yaml/ {f=1; next} /^```/ {if (f) exit} f'; }

# yaml_field <key>   flat "key: value  # comment" scalar, from stdin. Prints nothing if absent.
yaml_field() {
  awk -v k="$1" '
    $0 ~ "^"k":" {
      sub("^"k":[[:space:]]*", ""); sub("[[:space:]]+#.*$", ""); gsub(/^"|"$/, ""); print; exit
    }'
}

# yaml_nested <parent> <key>   "parent:\n  key: value" scalar, from stdin.
yaml_nested() {
  awk -v p="$1" -v k="$2" '
    $0 ~ "^"p":[[:space:]]*$" {in_p=1; next}
    in_p && /^[^[:space:]]/ {in_p=0}
    in_p && $0 ~ "^[[:space:]]+"k":" {
      sub("^[[:space:]]+"k":[[:space:]]*", ""); sub("[[:space:]]+#.*$", ""); print; exit
    }'
}

# yaml_list <key>   block list "key:\n  - a\n  - b", from stdin. Flow lists ("key: [a, b]") are not read.
yaml_list() {
  awk -v k="$1" '
    $0 ~ "^"k":[[:space:]]*$" {f=1; next}
    f && /^[[:space:]]*-[[:space:]]/ {
      sub(/^[[:space:]]*-[[:space:]]*/, ""); sub(/[[:space:]]+#.*$/, ""); gsub(/^"|"$/, ""); gsub(/^\047|\047$/, ""); print; next
    }
    f && /^[[:space:]]*$/ {next}
    f && /^[^[:space:]]/ {exit}'
}

result_status() { jq -r '.body' <<<"$1" | yaml_block | yaml_field status; }
result_created() { jq -r '.created_at' <<<"$1"; }
result_tasks() { jq -r '.body' <<<"$1" | yaml_block | yaml_nested tasks "$2"; }

# ---------------------------------------------------------------- dispatching agents
# dispatch <workflow> <thread> [-f input=value ...]
# <thread> is where the agent posts its result: the request issue before the plan pull request exists,
# the plan pull request after.
dispatch() {
  local wf="$1" thread="$2"; shift 2
  log "dispatch $wf for $TARGET#$ISSUE, result on #$thread"
  eng workflow run "$wf.lock.yml" --repo "$ENGINE_REPO" --ref "$ENGINE_REF" \
    -f target="$TARGET" -f issue="$ISSUE" -f thread="$thread" "$@"
}

plan_pr() {
  tgt api "repos/$TARGET/pulls?head=${TARGET%%/*}:forge/issue-$ISSUE&state=open" --jq '.[0].number // empty'
}

fail_stage() {
  log "blocked: $1"
  set_state blocked
  comment "**Blocked.** $1

See the orchestrator run: $RUN_URL"
}

refuse() {
  log "refused: $1"
  remove_label ready-for-dev
  comment "**Not approved.** $1"
}

# ---------------------------------------------------------------- authority (ADR-0011)
# A human (not a bot) with write access to the target. Checked here, never taken from the payload's claims.
require_human_writer() {
  local u="$1" type perm
  [ -n "$u" ] || { log "no sender"; return 1; }
  type=$(tgt api "users/$u" --jq .type 2>/dev/null) || { log "cannot resolve $u"; return 1; }
  [ "$type" = "User" ] || { log "$u is a $type, not a human"; return 1; }
  perm=$(tgt api "repos/$TARGET/collaborators/$u/permission" --jq .permission 2>/dev/null) || { log "no permission for $u"; return 1; }
  case "$perm" in
    admin | write) return 0 ;;
    *) log "$u has $perm access, write is required"; return 1 ;;
  esac
}

# ---------------------------------------------------------------- approval policy (ADR-0012)
lc() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }

# Lowercase, no leading ./ or /, no trailing /. Case-insensitive on purpose: stricter, and safe on any filesystem.
norm_path() {
  local p
  p=$(lc "$1"); p="${p#./}"; p="${p#/}"
  printf '%s' "${p%/}"
}

# The person who opened the request issue.
requester() { tgt api "repos/$TARGET/issues/$ISSUE" --jq '.user.login' 2>/dev/null; }

# The sender's role on the target: admin, maintain, write, triage, read or a custom role.
approver_role() { tgt api "repos/$TARGET/collaborators/$1/permission" --jq '.role_name' 2>/dev/null; }

# Every path the plan declares plus every file changed on the plan branch. The wider of the two, so a plan
# cannot hide a path by leaving it out of its own list.
plan_paths() {
  {
    jq -r '.body' <<<"$1" | yaml_block | yaml_list paths
    tgt api "repos/$TARGET/pulls/$2/files" --paginate --jq '.[].filename'
  } | sort -u
}

# Reads paths on stdin and prints those under a sensitive prefix. A path with ".." is a hit, because it could
# point outside the prefix it appears to be under. Matches whole path segments: "infra/" does not match
# "infra-docs/x".
sensitive_hits() {
  local prefixes p s n
  prefixes=$(printf '%s\n' "${FORGE_SENSITIVE_PATHS:-}" | tr ',' '\n')
  while IFS= read -r p; do
    [ -n "$p" ] || continue
    n=$(norm_path "$p")
    case "/$n/" in *"/../"*) printf '%s\n' "$p"; continue ;; esac
    while IFS= read -r s; do
      s=$(norm_path "$(printf '%s' "$s" | tr -d '[:space:]')")
      [ -n "$s" ] || continue
      if [ "$n" = "$s" ] || [[ "$n" == "$s"/* ]]; then printf '%s\n' "$p"; break; fi
    done <<<"$prefixes"
  done
}

# ---------------------------------------------------------------- scope gate before review
# The agents' write scope is enforced after the fact: every changed file must be in the plan directory or in
# the paths the target declares for application and infrastructure code. Without a config, nothing is allowed.
scope_violations() {
  local pr="$1" cfg allowed f ok p
  cfg=$(tgt api "repos/$TARGET/contents/.forge/config.yml" --jq .content 2>/dev/null | base64 -d 2>/dev/null || true)
  allowed=""
  if [ -n "$cfg" ]; then
    allowed=$(printf '%s\n' "$cfg" | yq '((.paths.app // []) + (.paths.infra // []))[]' 2>/dev/null || true)
  fi
  tgt api "repos/$TARGET/pulls/$pr/files" --paginate --jq '.[].filename' | while IFS= read -r f; do
    case "$f" in .forge/requests/"$ISSUE"/*) continue ;; esac
    ok=0
    while IFS= read -r p; do
      [ -n "$p" ] || continue
      p="${p%/}/"
      case "$f" in "$p"*) ok=1; break ;; esac
    done <<<"$allowed"
    [ "$ok" -eq 1 ] || printf '%s\n' "$f"
  done
}

start_review() {
  local pr="$1" bad
  bad=$(scope_violations "$pr")
  if [ -n "$bad" ]; then
    fail_stage "The implementation changed files outside the paths this target allows (\`paths.app\`, \`paths.infra\` in \`.forge/config.yml\`):

$(printf '%s\n' "$bad" | sed 's/^/- `/; s/$/`/')"
    return 0
  fi
  dispatch forge-reviewer "$pr" -f pr="$pr" -f ref="forge/issue-$ISSUE"
}

# ---------------------------------------------------------------- human events
start_request() {
  has_label forge || { log "forge label is not on the issue any more"; return 0; }
  local l busy=0
  for l in needs-clarification planning spec-review needs-review; do
    if has_label "$l"; then busy=1; fi
  done
  if [ "$busy" -eq 1 ]; then log "request already in progress"; return 0; fi
  ensure_labels
  clear_state
  dispatch forge-ba "$ISSUE"
}

approve() {
  has_label spec-review || { refuse "The plan is not awaiting approval."; return 0; }

  # Two people per change: whoever opened the request may not approve its plan, unless the engine allows it.
  if [ "${FORGE_ALLOW_SELF_APPROVAL:-false}" != true ]; then
    local req
    req=$(requester) || req=""
    [ -n "$req" ] || { refuse "Could not tell who opened this request, so the approval is denied."; return 0; }
    if [ "$(lc "$req")" = "$(lc "$P_SENDER")" ]; then
      refuse "You opened this request, so another person with write access must approve the plan."
      return 0
    fi
  fi

  local pr plan status app infra
  pr=$(plan_pr)
  [ -n "$pr" ] || { refuse "No open plan pull request was found for branch \`forge/issue-$ISSUE\`."; return 0; }
  plan=$(latest_result planner)
  [ -n "$plan" ] || { refuse "No planner result was found."; return 0; }
  status=$(result_status "$plan")
  [ "$status" = "ready-for-review" ] || { refuse "The latest plan is not ready for review."; return 0; }

  # Sensitive paths need a stronger approver. Fails closed: if the paths or the role cannot be read, deny.
  if [ -n "${FORGE_SENSITIVE_PATHS:-}" ]; then
    local paths hits role
    if ! paths=$(plan_paths "$plan" "$pr"); then
      refuse "Could not list the files this plan changes, so the approval is denied."
      return 0
    fi
    hits=$(printf '%s\n' "$paths" | sensitive_hits)
    if [ -n "$hits" ]; then
      role=$(approver_role "$P_SENDER") || role=""
      case "$role" in
        admin | maintain) ;;
        *)
          refuse "This plan touches sensitive paths, so it must be approved by someone with the admin or maintain role (you have: \`${role:-unknown}\`):

$(printf '%s\n' "$hits" | head -10 | sed 's/^/- `/; s/$/`/')"
          return 0 ;;
      esac
    fi
  fi

  app=$(result_tasks "$plan" app); [[ "$app" =~ ^[0-9]+$ ]] || app=0
  infra=$(result_tasks "$plan" infra); [[ "$infra" =~ ^[0-9]+$ ]] || infra=0
  remove_label spec-review
  if [ "$app" -gt 0 ]; then
    dispatch forge-coder "$pr" -f pr="$pr" -f ref="forge/issue-$ISSUE"
  elif [ "$infra" -gt 0 ]; then
    dispatch forge-infra "$pr" -f pr="$pr" -f ref="forge/issue-$ISSUE"
  else
    fail_stage "The plan has no application or infrastructure tasks."
  fi
}

resume_after_clarification() {
  has_label needs-clarification || { log "not waiting for clarification"; return 0; }
  local ba planner agent pr
  ba=$(latest_result ba); planner=$(latest_result planner)
  agent=ba
  if [ -n "$planner" ] && { [ -z "$ba" ] || [[ "$(result_created "$planner")" > "$(result_created "$ba")" ]]; }; then
    agent=planner
  fi
  remove_label needs-clarification
  if [ "$agent" = planner ]; then
    set_state planning
    pr=$(plan_pr)
    if [ -n "$pr" ]; then
      dispatch forge-planner "$pr" -f pr="$pr" -f ref="forge/issue-$ISSUE"
    else
      dispatch forge-planner "$ISSUE"
    fi
  else
    dispatch forge-ba "$ISSUE"
  fi
}

handle_human() {
  require_human_writer "$P_SENDER" || { log "ignored: sender is not an authorized human"; return 0; }
  case "$P_EVENT/$P_ACTION" in
    issues/labeled)
      case "$P_LABEL" in
        forge) start_request ;;
        ready-for-dev) approve ;;
        *) log "ignored label $P_LABEL" ;;
      esac ;;
    issue_comment/created) resume_after_clarification ;;
    *) log "ignored $P_EVENT/$P_ACTION" ;;
  esac
}

# ---------------------------------------------------------------- agent results
# True when the agent's latest result is at least as new as the run that just finished.
fresh() { [[ "$(result_created "$1")" > "$RUN_STARTED_AT" || "$(result_created "$1")" == "$RUN_STARTED_AT" ]]; }

handle_agent() {
  if [ "$CONCLUSION" != success ]; then
    fail_stage "The \`$AGENT\` run ended with \`$CONCLUSION\`."
    return 0
  fi
  local res status pr plan infra
  res=$(latest_result "$AGENT")
  if [ -z "$res" ] || ! fresh "$res"; then
    fail_stage "The \`$AGENT\` run produced no result comment."
    return 0
  fi
  status=$(result_status "$res")
  log "$AGENT -> $status"

  case "$AGENT/$status" in
    ba/complete)
      set_state planning
      dispatch forge-planner "$ISSUE" ;;
    planner/ready-for-review)
      pr=$(plan_pr)
      if [ -z "$pr" ]; then fail_stage "The planner reported a plan but no pull request exists for \`forge/issue-$ISSUE\`."; return 0; fi
      remove_label ready-for-dev
      set_state spec-review ;;
    coder/ready-for-review)
      pr=$(plan_pr); plan=$(latest_result planner)
      infra=$(result_tasks "$plan" infra); [[ "$infra" =~ ^[0-9]+$ ]] || infra=0
      if [ "$infra" -gt 0 ]; then
        dispatch forge-infra "$pr" -f pr="$pr" -f ref="forge/issue-$ISSUE"
      else
        start_review "$pr"
      fi ;;
    infra/ready-for-review)
      start_review "$(plan_pr)" ;;
    reviewer/pass)
      set_state needs-review ;;
    reviewer/changes-requested)
      pr=$(plan_pr)
      remove_label ready-for-dev
      set_state planning
      dispatch forge-planner "$pr" -f pr="$pr" -f ref="forge/issue-$ISSUE" ;;
    ba/needs-clarification | planner/needs-clarification)
      set_state needs-clarification ;;
    */blocked)
      fail_stage "The \`$AGENT\` agent reported that it is blocked. Its result comment explains why." ;;
    *)
      fail_stage "The \`$AGENT\` agent reported an unknown status \`$status\`." ;;
  esac
}

main() {
  case "$FORGE_KIND" in
    human) handle_human ;;
    agent) handle_agent ;;
    *) log "unknown kind $FORGE_KIND"; exit 1 ;;
  esac
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main; fi
