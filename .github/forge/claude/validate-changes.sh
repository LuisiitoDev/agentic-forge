#!/usr/bin/env bash
# Validates the changes an agent made before anything is published. Reads the changed paths on stdin, one per line.
# Usage: validate-changes.sh <agent> <issue> <patch>      Env: ALLOWED (coder/infra: newline list of path prefixes)
# Exit 0 and no output when the change is acceptable; otherwise prints one reason per line and exits 1.
set -euo pipefail
agent="$1"; issue="$2"; patch="$3"
[[ "$issue" =~ ^[0-9]+$ ]] || { echo "bad issue"; exit 1; }
bad=0; n=0
fail() { echo "$1"; bad=1; }
# Normalizes a prefix the same way for every comparison: no ./, no leading or trailing /.
norm() { local p="${1#./}"; p="${p#/}"; printf '%s' "${p%/}"; }
while IFS= read -r p; do
  [ -n "$p" ] || continue
  n=$((n + 1))
  case "$p" in
    /* | *".."* | *$'\t'* | *\\*) fail "unsafe path: $p"; continue ;;
  esac
  case "/$p/" in
    */.git/*) fail "touches .git: $p"; continue ;;
  esac
  case "$p" in
    .github | .github/*) fail "touches .github: $p"; continue ;;
    .forge/config.yml) fail "touches .forge/config.yml: $p"; continue ;;
  esac
  case "$agent" in
    planner)
      case "$p" in ".forge/requests/$issue/"*) ;; *) fail "outside .forge/requests/$issue/: $p" ;; esac ;;
    coder | infra)
      ok=0
      while IFS= read -r a; do
        a=$(norm "$a"); [ -n "$a" ] || continue
        if [ "$p" = "$a" ] || [[ "$p" == "$a"/* ]]; then ok=1; break; fi
      done <<<"${ALLOWED:-}"
      [ "$ok" = 1 ] || fail "outside the paths this target allows for $agent: $p" ;;
    *) fail "$agent may not change files: $p" ;;
  esac
done
[ "$n" -le 500 ] || fail "too many changed files ($n)"
if [ -s "$patch" ]; then
  [ "$(wc -c < "$patch")" -le 5242880 ] || fail "patch larger than 5 MiB"
  # Symlinks (120000) and submodules (160000) are never accepted.
  if grep -Eq '^(new file mode|new mode|old mode|index [0-9a-f.]+ )?(120000|160000)' "$patch" || grep -Eq '^(new file mode|deleted file mode|new mode|old mode) (120000|160000)$' "$patch"; then
    fail "the change contains a symlink or a submodule"
  fi
fi
exit "$bad"
