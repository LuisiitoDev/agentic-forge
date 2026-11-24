#!/usr/bin/env bash
# Tests of the scripts the Claude agent workflows use to validate what an agent changed (no GitHub, no network).
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
S="$ROOT/.github/forge/claude"
pass=0; fail=0
ok() { # name expected_rc actual_rc
  if [ "$2" = "$3" ]; then pass=$((pass+1)); else fail=$((fail+1)); echo "FAIL $1: expected rc=$2 got rc=$3"; fi; }
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
mk() { rm -rf "$T/base" "$T/work"; mkdir -p "$T/base/src" "$T/work/src"; echo a > "$T/base/src/a.txt"; cp "$T/base/src/a.txt" "$T/work/src/"; }
collect() { bash "$S/collect-changes.sh" "$T/base" "$T/work" "$T/c.patch" "$T/names.txt"; ok "collect-changes exits 0" 0 "$?"; }
validate() { # agent issue [ALLOWED]
  ALLOWED="${3:-}" bash "$S/validate-changes.sh" "$1" "$2" "$T/c.patch" < "$T/names.txt" > "$T/reasons.txt"; echo $?; }

# planner: only .forge/requests/<issue>/
mk; mkdir -p "$T/work/.forge/requests/7"; echo p > "$T/work/.forge/requests/7/plan.md"; collect
ok "planner inside its folder" 0 "$(validate planner 7)"
ok "planner for another issue" 1 "$(validate planner 8)"
mk; mkdir -p "$T/work/.forge/requests/7"; echo p > "$T/work/.forge/requests/7/plan.md"; echo b >> "$T/work/src/a.txt"; collect
ok "planner outside its folder" 1 "$(validate planner 7)"

# coder and infra: only the prefixes of the target's configuration; empty means read-only
mk; echo b >> "$T/work/src/a.txt"; collect
ok "coder inside paths.app" 0 "$(validate coder 7 'src/')"
ok "coder with an empty list" 1 "$(validate coder 7 '')"
ok "coder outside paths.app" 1 "$(validate coder 7 'docs/')"
ok "infra with ./ and a trailing slash" 0 "$(validate infra 7 './src/')"
ok "a prefix is a whole segment" 1 "$(validate coder 7 'sr')"

# never accepted, whatever the prefix
mk; mkdir -p "$T/work/.github/workflows"; echo x > "$T/work/.github/workflows/w.yml"; collect
ok ".github is refused" 1 "$(validate coder 7 '.github/')"
mk; mkdir -p "$T/work/.forge"; echo x > "$T/work/.forge/config.yml"; collect
ok ".forge/config.yml is refused" 1 "$(validate coder 7 '.forge/')"
mk; ln -s /etc/passwd "$T/work/src/link"; collect
ok "a symlink is refused" 1 "$(validate coder 7 'src/')"
mk; echo x > "$T/work/src/..hidden"; collect
ok "a path with .. is refused" 1 "$(validate coder 7 'src/')"
mk; mkdir -p "$T/work/src/deep/.git"; echo x > "$T/work/src/deep/.git/config"; collect
ok "a nested .git is ignored, not compared" 0 "$(validate coder 7 'src/'; )"

# no change at all: still exit 0, empty patch
mk; collect; ok "no change gives an empty patch" 0 "$([ -s "$T/c.patch" ] && echo 1 || echo 0)"

# reviewer and ba never change files
mk; echo b >> "$T/work/src/a.txt"; collect
ok "reviewer may not change files" 1 "$(validate reviewer 7 'src/')"
ok "ba may not change files" 1 "$(validate ba 7 'src/')"

# the agent's own repository configuration is never executed
mk; mkdir -p "$T/work/.git"; echo "[core] fsmonitor = touch $T/PWNED" > "$T/work/.git/config"; echo b >> "$T/work/src/a.txt"; collect
ok "git config of the agent is not run" 0 "$([ -e "$T/PWNED" ] && echo 1 || echo 0)"

# the patch applies to a fresh copy of the base with -p2
mk; echo b >> "$T/work/src/a.txt"; echo n > "$T/work/src/new.txt"; collect
rm -rf "$T/fresh"; mkdir -p "$T/fresh"; cp -r "$T/base/src" "$T/fresh/src"
( cd "$T/fresh" && git init -q . && git apply -p2 "$T/c.patch" ); ok "patch applies with -p2" 0 "$?"
ok "applied tree equals the work tree" 0 "$(diff -r "$T/fresh/src" "$T/work/src" >/dev/null 2>&1; echo $?)"

# the prompt of every agent is built, strips the spec frontmatter and ends with the override
for a in ba planner coder infra reviewer; do
  ( cd "$ROOT/.github/.." && ENGINE_DIR=. TARGET=o/r ISSUE=7 THREAD=7 PR=9 REF=forge/issue-7 bash "$S/build-prompt.sh" $a > "$T/prompt-$a.md" ); ok "prompt $a builds" 0 "$?"
  ok "prompt $a has no frontmatter" 0 "$(grep -c '^capabilities:' "$T/prompt-$a.md" | sed 's/^0$/0/;t;s/.*/1/')"
  ok "prompt $a has the override" 0 "$(grep -q 'Delivery override (this workflow)' "$T/prompt-$a.md"; echo $?)"
done
ok "prompt rejects an unknown agent" 1 "$(ENGINE_DIR=$ROOT TARGET=o/r ISSUE=7 THREAD=7 bash "$S/build-prompt.sh" nope >/dev/null 2>&1; echo $?)"

echo "passed=$pass failed=$fail"; [ "$fail" -eq 0 ]
