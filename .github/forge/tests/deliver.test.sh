#!/usr/bin/env bash
# Integration test of the delivery script of the Claude agents: a local bare repository stands in for the target and a
# fake gh for GitHub. Checks what is pushed, which PR is opened, what is commented, and what is refused.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
S="$ROOT/.github/forge/claude"
pass=0; fail=0
ok() { if [ "$2" = "$3" ]; then pass=$((pass+1)); else fail=$((fail+1)); echo "FAIL $1: expected [$2] got [$3]"; fi; }
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
export FAKE_DIR="$W/fake"; mkdir -p "$W/bin"; cp "$ROOT/.github/forge/tests/bin/gh-deliver" "$W/bin/gh"; export PATH="$W/bin:$PATH"
export GIT_CONFIG_GLOBAL="$W/gitconfig" GIT_CONFIG_SYSTEM=/dev/null; git config --global init.defaultBranch main; git config --global protocol.file.allow always

setup() { # fresh remote with a main branch, a clone in $W/ws/target, empty artifacts and fake state
  rm -rf "$W/remote.git" "$W/ws" "$FAKE_DIR"; mkdir -p "$W/ws/artifacts" "$FAKE_DIR"
  git init -q --bare "$W/remote.git"
  git clone -q "$W/remote.git" "$W/seed" 2>/dev/null; ( cd "$W/seed"
    mkdir -p src .forge; echo a > src/a.txt; printf 'paths:\n  app:\n    - src/\n  infra:\n    - infra/\n' > .forge/config.yml
    git add -A; git -c user.name=t -c user.email=t@t commit -qm seed; git push -q origin main ); rm -rf "$W/seed"
  git clone -q "$W/remote.git" "$W/ws/target"
}
mkpatch() { # <command that edits $W/work>; builds artifacts/changes.patch and names.txt from the target as it is
  rm -rf "$W/base" "$W/work"; mkdir -p "$W/base"; ( cd "$W/ws/target" && git archive HEAD ) | tar -x -C "$W/base"; cp -a "$W/base" "$W/work"
  ( cd "$W/work" && eval "$1" ); bash "$S/collect-changes.sh" "$W/base" "$W/work" "$W/ws/artifacts/changes.patch" "$W/ws/artifacts/names.txt"
}
result() { printf '## %s result: %s\n\n```yaml\nstatus: %s\n```\n\nBody <!-- hidden -->visible\n' "$1" "$2" "$2" > "$W/ws/artifacts/result.md"; }
deliver() { # agent [PR] [REF]  -> exit code
  ( cd "$W/ws"; AGENT=$1 TARGET=o/r ISSUE=7 THREAD=${THREAD:-7} PR=${2:-} REF=${3:-} GH_TOKEN=t ART=artifacts ENGINE_DIR="$ROOT" bash "$S/deliver.sh" ) > "$W/out.log" 2>&1; echo $?; }
remote_has() { git --git-dir="$W/remote.git" cat-file -e "refs/heads/$1:$2" 2>/dev/null && echo yes || echo no; }
branch_exists() { git --git-dir="$W/remote.git" show-ref --verify -q "refs/heads/$1" && echo yes || echo no; }
plan_branch() { # a plan branch on the remote, as the Planner leaves it
  ( cd "$W/ws/target" && git checkout -q -b forge/issue-7 && mkdir -p .forge/requests/7 && echo plan > .forge/requests/7/plan.md && git add -A && git -c user.name=t -c user.email=t@t commit -qm plan && git push -q origin forge/issue-7 && git checkout -q main ); }
pr_json() { printf '{"headRefName":"%s","title":"%s","state":"%s","isCrossRepository":%s}\n' "${1:-forge/issue-7}" "${2:-[forge] Add a hello file}" "${3:-OPEN}" "${4:-false}" > "$FAKE_DIR/pr_view.json"; }

# 1 planner, first pass: branch pushed, draft PR opened with the prefix, result commented on the issue
setup; mkpatch 'mkdir -p .forge/requests/7 && echo plan > .forge/requests/7/plan.md'; result Planner ready-for-review
ok "planner first pass exits 0" 0 "$(deliver planner)"
ok "planner branch has the plan" yes "$(remote_has forge/issue-7 .forge/requests/7/plan.md)"
ok "planner opened one draft PR" 1 "$(grep -c -- '--draft' "$FAKE_DIR/created.log")"
ok "planner PR title has the prefix" 1 "$(grep -c -- '--title \[forge\] Add a hello file' "$FAKE_DIR/created.log")"
ok "planner commented the result" 1 "$(grep -c '^## Planner result: ready-for-review' "$FAKE_DIR/comment.md")"
ok "HTML comments are removed from the result" 0 "$(grep -c 'hidden' "$FAKE_DIR/comment.md")"

# 2 planner rerun with the PR already open: no second PR (idempotent)
setup; plan_branch; echo 9 > "$FAKE_DIR/open_pr"; mkpatch 'mkdir -p .forge/requests/7 && echo plan2 > .forge/requests/7/tasks.md'
( cd "$W/ws/target" && git checkout -q -b forge/issue-7 origin/forge/issue-7 2>/dev/null; git checkout -q main ); result Planner ready-for-review
ok "planner rerun exits 0" 0 "$(deliver planner)"
ok "planner rerun opens no second PR" 0 "$([ -f "$FAKE_DIR/created.log" ] && wc -l < "$FAKE_DIR/created.log" || echo 0)"
ok "planner rerun pushed to the existing branch" yes "$(remote_has forge/issue-7 .forge/requests/7/tasks.md)"

# 3 planner outside its folder: nothing pushed, a blocked result is commented instead
setup; mkpatch 'echo x >> src/a.txt'; result Planner ready-for-review
ok "planner outside its folder exits 0" 0 "$(deliver planner)"
ok "planner outside its folder: no branch" no "$(branch_exists forge/issue-7)"
ok "planner outside its folder: blocked comment" 1 "$(grep -c '^## Planner result: blocked' "$FAKE_DIR/comment.md")"
ok "the blocked comment names the reason" 1 "$(grep -c 'outside .forge/requests/7/' "$FAKE_DIR/comment.md")"

# 4 coder inside paths.app: pushed to the plan branch
setup; plan_branch; pr_json; mkpatch 'echo b >> src/a.txt'; ( cd "$W/ws/target" && git fetch -q origin ); result Coder ready-for-review
ok "coder exits 0" 0 "$(THREAD=9 deliver coder 9 forge/issue-7)"
ok "coder pushed to the plan branch" yes "$(git --git-dir="$W/remote.git" show forge/issue-7:src/a.txt >/dev/null 2>&1 && echo yes || echo no)"
ok "coder change is in the plan branch" b "$(git --git-dir="$W/remote.git" show forge/issue-7:src/a.txt | tail -1)"
ok "coder did not touch main" a "$(git --git-dir="$W/remote.git" show main:src/a.txt | tail -1)"
ok "coder commented the result" 1 "$(grep -c '^## Coder result: ready-for-review' "$FAKE_DIR/comment.md")"

# 5 coder outside paths.app (infra/ is not its path): rejected, blocked comment, nothing pushed
setup; plan_branch; pr_json; mkpatch 'mkdir -p infra && echo x > infra/main.tf'; result Coder ready-for-review
ok "coder outside paths exits 0" 0 "$(THREAD=9 deliver coder 9 forge/issue-7)"
ok "coder outside paths: nothing pushed" no "$(remote_has forge/issue-7 infra/main.tf)"
ok "coder outside paths: blocked comment" 1 "$(grep -c '^## Coder result: blocked' "$FAKE_DIR/comment.md")"

# 6 infra inside paths.infra works; a workflow file never does
setup; plan_branch; pr_json; mkpatch 'mkdir -p infra && echo x > infra/main.tf'; result Infra ready-for-review
ok "infra exits 0" 0 "$(THREAD=9 deliver infra 9 forge/issue-7)"
ok "infra pushed" yes "$(remote_has forge/issue-7 infra/main.tf)"
setup; plan_branch; pr_json; mkpatch 'mkdir -p .github/workflows && echo x > .github/workflows/w.yml'; result Infra ready-for-review
ok "infra workflow change exits 0" 0 "$(THREAD=9 deliver infra 9 forge/issue-7)"
ok "infra workflow change is not pushed" no "$(remote_has forge/issue-7 .github/workflows/w.yml)"

# 7 the PR is checked before anything is pushed
for case_ in "other/branch|[forge] x|OPEN|false" "forge/issue-7|no prefix|OPEN|false" "forge/issue-7|[forge] x|CLOSED|false" "forge/issue-7|[forge] x|OPEN|true"; do
  IFS='|' read -r h t s c <<<"$case_"
  setup; plan_branch; pr_json "$h" "$t" "$s" "$c"; mkpatch 'echo b >> src/a.txt'; result Coder ready-for-review
  rc=$(THREAD=9 deliver coder 9 forge/issue-7)
  ok "refused PR [$h|$t|$s|$c]: exit non-zero" 1 "$([ "$rc" != 0 ] && echo 1 || echo 0)"
  ok "refused PR [$h|$t|$s|$c]: nothing pushed" a "$(git --git-dir="$W/remote.git" show forge/issue-7:src/a.txt 2>/dev/null | tail -1 || echo a)"
done

# 8 a blocked result publishes no code; the reviewer never publishes code
setup; plan_branch; pr_json; mkpatch 'echo b >> src/a.txt'; result Coder blocked
ok "blocked coder exits 0" 0 "$(THREAD=9 deliver coder 9 forge/issue-7)"
ok "blocked coder pushed nothing" a "$(git --git-dir="$W/remote.git" show forge/issue-7:src/a.txt 2>/dev/null | tail -1 || echo a)"
setup; plan_branch; pr_json; mkpatch 'echo b >> src/a.txt'; result Reviewer pass
ok "reviewer exits 0" 0 "$(THREAD=9 deliver reviewer 9 forge/issue-7)"
ok "reviewer changes are discarded" 0 "$(git --git-dir="$W/remote.git" show forge/issue-7:src/a.txt 2>/dev/null | grep -c '^b$')"
ok "reviewer result is commented" 1 "$(grep -c '^## Reviewer result: pass' "$FAKE_DIR/comment.md")"

# 9 a result with the wrong heading is refused and nothing is posted
setup; mkpatch 'true'; printf 'hello\n' > "$W/ws/artifacts/result.md"
rc=$(deliver ba); ok "wrong heading: exit non-zero" 1 "$([ "$rc" != 0 ] && echo 1 || echo 0)"; ok "wrong heading: nothing posted" no "$([ -f "$FAKE_DIR/comment.md" ] && echo yes || echo no)"
setup; mkpatch 'true'; printf '## Coder result: complete\n' > "$W/ws/artifacts/result.md"
rc=$(deliver ba); ok "another agent's heading: exit non-zero" 1 "$([ "$rc" != 0 ] && echo 1 || echo 0)"

echo "passed=$pass failed=$fail"; [ "$fail" -eq 0 ]
