#!/usr/bin/env bash
# Turns what the agent changed in target/ into a patch, without trusting target/.git (the agent may have rewritten
# its config or hooks, and git would run them). Compares the pristine copy taken before the run with the final tree.
# Usage: collect-changes.sh <base-dir> <work-dir> <patch-out> <names-out>
# The patch applies with `git apply -p2` (its paths are a/a/<path> and b/b/<path>). names-out lists the changed paths.
set -euo pipefail
base=$(realpath "$1"); work=$(realpath "$2"); patch=$(realpath -m "$3"); names=$(realpath -m "$4")
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/a" "$tmp/b"
cp -a "$base/." "$tmp/a/"
cp -a "$work/." "$tmp/b/"
# No .git at any depth: nested repositories and their configuration are never read or compared.
find "$tmp/a" "$tmp/b" -name .git -prune -exec rm -rf {} +
cd "$tmp"
# Outside any repository and with no configuration: nothing of the agent's is read.
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null GIT_CONFIG_NOSYSTEM=1
# `git diff --no-index` exits 1 when the trees differ (it implies --exit-code): that is the normal case here.
git diff --no-index --binary --no-renames --no-color a b > "$patch" || [ "$?" = 1 ]
{ git diff --no-index --no-renames --name-only -z a b || [ "$?" = 1 ]; } | tr '\0' '\n' | sed -E 's#^[ab]/##' | sed '/^$/d' | sort -u > "$names"
