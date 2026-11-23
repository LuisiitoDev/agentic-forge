#!/usr/bin/env bash
# Agentic Forge orchestrator. Deterministic routing: no model is involved here.
#
# Two kinds of events are handled (see .github/workflows/forge-orchestrator.yml):
#   human  - a repository_dispatch from the intake workflow: a human labeled or commented on a request issue
#   agent  - a forge-* agent workflow finished: read its result and decide the next stage
# The workflow only receives human events. With FORGE_AWAIT=1 the script then waits for each agent it dispatches
# and handles the result itself, as an `agent` event, until a human gate or a failure ends the chain (ADR-0018).
# GitHub does not deliver `workflow_run` for runs started with the workflow token, so there is no agent event.
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
# agent:  AGENT (ba|planner|coder|infra|reviewer), CONCLUSION, RUN_STARTED_AT (set by the wait; the tests set them)
# Waiting for agent runs (ADR-0018), all optional:
#   FORGE_AWAIT          "1" turns the wait on. Default: off
#   AWAIT_INTERVAL       seconds between polls. Default: 20
#   AWAIT_FIND_TRIES     polls to find the dispatched run before giving up. Default: 9
#   AWAIT_MAX            seconds to wait for one agent run to complete. Default: 5400
#   FORGE_MAX_STAGES     agent runs awaited in one orchestration. Default: 12
#   FORGE_TOKEN_BUDGET   seconds since the script started after which it stops waiting. The target token is a
#                        GitHub App installation token and expires after one hour, so it must still be valid when
#                        the request is marked blocked. Default: 3300
#   FORGE_NOW            the dispatch time (ISO 8601 UTC), for the tests. Default: the clock
# Retrying an agent run the model provider rejected with 429 Too Many Requests (ADR-0020), all optional:
#   FORGE_RATE_LIMIT_ATTEMPTS   runs of one stage in total, the first one included. Default: 4
#   FORGE_RETRY_BASE            seconds of the first wait; each retry doubles it. Default: 60
#   FORGE_RETRY_CAP             longest wait in seconds. Default: 600
#   FORGE_RATE_LIMIT_PATTERN    extended regex, case-insensitive, that marks a failed run's log as rate limited
#   FORGE_RATE_LIMIT_EXCLUDE    extended regex that wins over the pattern: a 429 that waiting does not fix

set -euo pipefail
export LC_ALL=C

ORCH_STARTED=$SECONDS
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
# Trusted = authored by the engine's bot and starting with the agent's result heading (`## BA result: complete`).
# The authenticity is the author. The heading only selects which comment. It cannot be an HTML comment: `gh aw`
# strips HTML comments from the body an agent publishes (checked on the first real run, 2026-11-23).
# Results live on the request issue (BA, first plan) and on the plan pull request (everything after), so both
# are read. A pull request's conversation comments are served by the issues API under its own number.
agent_title() { case "$1" in ba) echo BA ;; planner) echo Planner ;; coder) echo Coder ;; infra) echo Infra ;; reviewer) echo Reviewer ;; *) echo "$1" ;; esac; }

latest_result() {
  local pr
  pr=$(plan_pr)
  {
    tgt api "repos/$TARGET/issues/$ISSUE/comments" --paginate --jq '.[]'
    if [ -n "$pr" ]; then tgt api "repos/$TARGET/issues/$pr/comments" --paginate --jq '.[]'; fi
  } |
    jq -s --arg bot "$FORGE_BOT_LOGIN" --arg m "## $(agent_title "$1") result:" '
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
    -f target="$TARGET" -f issue="$ISSUE" -f thread="$thread" -f target_repo_name="${TARGET##*/}" "$@"
  # Remember it: with FORGE_AWAIT the orchestrator waits for this run (see `drive`), and dispatches it again with the
  # same inputs if the model provider rate limited it (`retry_after_rate_limit`).
  PENDING_AGENT="${wf#forge-}"
  PENDING_WF="$wf"; PENDING_THREAD="$thread"; PENDING_ARGS=("$@")
  PENDING_SINCE="${FORGE_NOW:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"
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

# ---------------------------------------------------------------- waiting for the agent runs (ADR-0018)
# The run a dispatch started, found by workflow file, creation time and the run-name the agent workflows set
# (`forge-<agent> OWNER/REPO#NUMBER`). The title is built from the validated target and issue and a fixed agent
# name, and a clock margin covers the difference between the runner's clock and GitHub's.
find_run() {
  local agent="$1" since="$2" margin
  [[ "$TARGET" =~ ^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$ && "$ISSUE" =~ ^[0-9]+$ && "$agent" =~ ^[a-z]+$ ]] || return 0
  margin=$(date -u -d "$since 30 seconds ago" +%Y-%m-%dT%H:%M:%SZ) || return 0
  eng api "repos/$ENGINE_REPO/actions/workflows/forge-$agent.lock.yml/runs?event=workflow_dispatch&per_page=30" \
    --jq "[.workflow_runs[] | select(.display_title == \"forge-$agent $TARGET#$ISSUE\" and .created_at >= \"$margin\")] | sort_by(.created_at) | last | .id // empty"
}

# await_run <agent> <since>   returns 0 when the run completed (and sets AGENT, CONCLUSION, RUN_STARTED_AT),
# 1 when no run appeared, 2 when it did not complete in time, 3 when the token budget ran out first.
await_run() {
  local agent="$1" since="$2" id="" i start="$SECONDS" line status conclusion started
  for ((i = 0; i < ${AWAIT_FIND_TRIES:-9}; i++)); do
    id=$(find_run "$agent" "$since") || id=""
    if [ -n "$id" ]; then break; fi
    sleep "${AWAIT_INTERVAL:-20}"
  done
  [ -n "$id" ] || return 1
  RUN_ID="$id"
  log "waiting for $agent run $id"
  while :; do
    line=$(eng api "repos/$ENGINE_REPO/actions/runs/$id" --jq '.status + " " + (.conclusion // "-") + " " + (.run_started_at // "-")') || line=""
    read -r status conclusion started <<<"$line" || true
    if [ "${status:-}" = completed ]; then break; fi
    if [ $((SECONDS - start)) -ge "${AWAIT_MAX:-5400}" ]; then return 2; fi
    if [ $((SECONDS - ORCH_STARTED)) -ge "${FORGE_TOKEN_BUDGET:-3300}" ]; then return 3; fi
    sleep "${AWAIT_INTERVAL:-20}"
  done
  AGENT="$agent"
  CONCLUSION="${conclusion/#-/}"
  RUN_STARTED_AT="${started/#-/$since}"
  log "$agent run $id completed: ${CONCLUSION:-unknown}"
}

# ---------------------------------------------------------------- rate limits (ADR-0020)
# A run the model provider rejected with 429 is dispatched again after a wait. Any other failure is not: it goes to
# `handle_agent`, which marks the request blocked, as before.
RATE_LIMIT_PATTERN_DEFAULT='429 Too Many Requests|rate_limit_error|"status": ?429|status code:? ?429|HTTP/[0-9.]+ 429'
# The gateway answers 429 when an agent spends its `max-turns` budget (ADR-0009). Waiting does not fix that.
RATE_LIMIT_EXCLUDE_DEFAULT='Maximum LLM invocations exceeded'

# num <value> <default>   the value if it is a whole number, else the default.
num() { if [[ "${1:-}" =~ ^[0-9]+$ ]]; then printf '%s' "$1"; else printf '%s' "$2"; fi; }

# rate_limited <run id>   true when the logs of the run's failed steps show a provider 429. Fails closed: a log that
# cannot be read is not a rate limit, so the run is not retried.
rate_limited() {
  local logs pat excl
  [[ "${1:-}" =~ ^[0-9]+$ ]] || return 1
  pat="${FORGE_RATE_LIMIT_PATTERN:-$RATE_LIMIT_PATTERN_DEFAULT}"
  excl="${FORGE_RATE_LIMIT_EXCLUDE-$RATE_LIMIT_EXCLUDE_DEFAULT}"
  logs=$(eng run view "$1" --repo "$ENGINE_REPO" --log-failed 2>/dev/null) || return 1
  if [ -n "$excl" ] && grep -Eiq -- "$excl" <<<"$logs"; then return 1; fi
  grep -Eiq -- "$pat" <<<"$logs"
}

# retry_delay <attempt>   seconds to wait before the next run: exponential backoff with jitter ("equal jitter").
# The wait doubles with each attempt up to the cap, and a random half of it is dropped, so that requests that were
# rate limited together do not all come back at the same moment. Result: between d/2 and d, d = min(cap, base*2^(n-1)).
retry_delay() {
  local n base cap d
  n=$(num "$1" 1); base=$(num "${FORGE_RETRY_BASE:-}" 60); cap=$(num "${FORGE_RETRY_CAP:-}" 600)
  d=$base
  while [ "$n" -gt 1 ] && [ "$d" -lt "$cap" ]; do d=$((d * 2)); n=$((n - 1)); done
  [ "$d" -le "$cap" ] || d=$cap
  echo $((d - d / 2 + RANDOM % (d / 2 + 1)))
}

# retry_after_rate_limit <attempt>   waits and dispatches the stage again. Every retry is written on the request
# issue so it can be audited. Returns 1, after marking the request blocked, when no attempt or no time is left.
retry_after_rate_limit() {
  local n="$1" max delay url
  max=$(num "${FORGE_RATE_LIMIT_ATTEMPTS:-}" 4)
  url="${GITHUB_SERVER_URL:-https://github.com}/$ENGINE_REPO/actions/runs/$RUN_ID"
  if [ "$n" -ge "$max" ]; then
    fail_stage "The \`$AGENT\` run was rate limited by the model provider (\`429 Too Many Requests\`) on all $max attempts. Last run: $url. Start the request again later."
    return 1
  fi
  delay=$(retry_delay "$n")
  if [ $((SECONDS - ORCH_STARTED + delay)) -ge "${FORGE_TOKEN_BUDGET:-3300}" ]; then
    fail_stage "The \`$AGENT\` run was rate limited by the model provider (\`429 Too Many Requests\`), and no time is left to retry before the orchestrator's token expires. Last run: $url. Start the request again later."
    return 1
  fi
  log "$AGENT run $RUN_ID was rate limited (attempt $n of $max), retrying in ${delay}s"
  comment "**Rate limited.** The \`$AGENT\` run was rejected by the model provider with \`429 Too Many Requests\` ($url). Retrying in $delay seconds (attempt $((n + 1)) of $max)."
  sleep "$delay"
  dispatch "$RETRY_WF" "$RETRY_THREAD" ${RETRY_ARGS[@]+"${RETRY_ARGS[@]}"}
}

# Follows the agents this orchestration dispatched, one after the other, until nothing is pending.
drive() {
  [ "${FORGE_AWAIT:-0}" = 1 ] || return 0
  local stages=0 rc agent since max="${FORGE_MAX_STAGES:-12}" attempt=1
  while [ -n "${PENDING_AGENT:-}" ]; do
    agent="$PENDING_AGENT"; since="$PENDING_SINCE"; PENDING_AGENT=""
    RETRY_WF="$PENDING_WF"; RETRY_THREAD="$PENDING_THREAD"; RETRY_ARGS=(${PENDING_ARGS[@]+"${PENDING_ARGS[@]}"})
    # A retry is the same stage again: it does not count against the stage limit.
    if [ "$attempt" -eq 1 ]; then stages=$((stages + 1)); fi
    if [ "$stages" -gt "$max" ]; then
      fail_stage "This request went through too many stages in one orchestration (limit $max)."
      return 0
    fi
    rc=0; await_run "$agent" "$since" || rc=$?
    case "$rc" in
      0)
        if [ "$CONCLUSION" != success ] && rate_limited "$RUN_ID"; then
          retry_after_rate_limit "$attempt" || return 0
          attempt=$((attempt + 1))
          continue
        fi
        attempt=1
        handle_agent ;;
      1) fail_stage "The \`$agent\` run did not start: no run titled \`forge-$agent $TARGET#$ISSUE\` appeared after the dispatch."; return 0 ;;
      2) fail_stage "The \`$agent\` run did not finish within ${AWAIT_MAX:-5400} seconds."; return 0 ;;
      *) fail_stage "The \`$agent\` run was still going when the orchestrator's token was about to expire (after ${FORGE_TOKEN_BUDGET:-3300} seconds), so nobody will read its result. See the \`$agent\` run in the engine's Actions tab, then start the request again."; return 0 ;;
    esac
  done
}

main() {
  case "$FORGE_KIND" in
    human) handle_human ;;
    agent) handle_agent ;;
    *) log "unknown kind $FORGE_KIND"; exit 1 ;;
  esac
  drive
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main; fi
