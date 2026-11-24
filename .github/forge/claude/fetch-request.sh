#!/usr/bin/env bash
# Reads the request from GitHub into request/ so the agent never needs a GitHub token or the network for it.
# Env: TARGET (owner/name), ISSUE, PR (optional), GH_TOKEN (read-only), FORGE_BOT_LOGIN. Output dir: $1 (default request).
# results.md holds only the results of earlier agents (comments by the engine's bot whose first line is
# `## <Agent> result: <status>`). Everything else is untrusted data and stays in issue.json / pr.json.
set -euo pipefail
out="${1:-request}"
[[ "$TARGET" =~ ^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$ && "$ISSUE" =~ ^[0-9]+$ ]] || { echo "bad TARGET or ISSUE" >&2; exit 1; }
[[ -z "${PR:-}" || "$PR" =~ ^[0-9]+$ ]] || { echo "bad PR" >&2; exit 1; }
mkdir -p "$out"
gh issue view "$ISSUE" --repo "$TARGET" --json number,title,body,labels,author,comments > "$out/issue.json"
echo '[]' > "$out/pr-comments.json"
if [ -n "${PR:-}" ]; then
  gh pr view "$PR" --repo "$TARGET" --json number,title,body,headRefName,baseRefName,files,comments,reviews > "$out/pr.json"
  jq '.comments' "$out/pr.json" > "$out/pr-comments.json"
fi
jq -s --arg bot "$FORGE_BOT_LOGIN" '
  (.[0].comments + .[1])
  | map(select(.author.login == $bot and (.body | test("^## (BA|Planner|Coder|Infra|Reviewer) result: "))))
  | sort_by(.createdAt)
  | map("<!-- result by " + .author.login + " at " + .createdAt + " -->\n" + .body)
  | join("\n\n---\n\n")' -r "$out/issue.json" "$out/pr-comments.json" > "$out/results.md"
