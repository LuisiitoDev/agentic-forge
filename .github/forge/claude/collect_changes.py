#!/usr/bin/env python3
"""Turns what the agent changed in target/ into a patch, without trusting target/.git.

The agent may have rewritten its repository configuration or hooks, and git would run them. So the pristine copy
taken before the run is compared with the final tree outside any repository, with no configuration, and with every
.git directory removed from both sides.

Usage: collect_changes.py <base-dir> <work-dir> <patch-out> <names-out>
The patch applies with `git apply -p2` (its paths are a/a/<path> and b/b/<path>). names-out lists the changed paths.
"""
import os
import shutil
import subprocess
import sys
import tempfile


# Caches and reports that test and lint tools write into the work tree. The target's .gitignore covers them, but this
# comparison does not read .gitignore, so without this list every run that ran the tests would publish them as changes.
_GENERATED = (".git", ".pytest_cache", ".ruff_cache", ".mypy_cache", "__pycache__", ".coverage")


def _copy_without_git(src: str, dst: str) -> None:
    shutil.copytree(src, dst, symlinks=True, ignore=shutil.ignore_patterns(*_GENERATED))


def _git_diff(cwd: str, *args: str) -> bytes:
    env = dict(os.environ, GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_SYSTEM="/dev/null", GIT_CONFIG_NOSYSTEM="1")
    # `git diff --no-index` exits 1 when the trees differ (it implies --exit-code): that is the normal case here.
    r = subprocess.run(["git", "diff", "--no-index", *args, "a", "b"], cwd=cwd, env=env, capture_output=True)
    if r.returncode not in (0, 1):
        raise RuntimeError(f"git diff failed ({r.returncode}): {r.stderr.decode('utf-8', 'replace')}")
    return r.stdout


def collect(base: str, work: str, patch_out: str, names_out: str) -> None:
    with tempfile.TemporaryDirectory() as tmp:
        _copy_without_git(base, os.path.join(tmp, "a"))
        _copy_without_git(work, os.path.join(tmp, "b"))
        patch = _git_diff(tmp, "--binary", "--no-renames", "--no-color")
        raw = _git_diff(tmp, "--no-renames", "--name-only", "-z")
    names = sorted({n[2:] for n in raw.decode("utf-8", "surrogateescape").split("\0") if n[2:]})
    with open(patch_out, "wb") as f:
        f.write(patch)
    with open(names_out, "w", encoding="utf-8", errors="surrogateescape") as f:
        f.write("".join(n + "\n" for n in names))


def main(argv) -> int:
    if len(argv) != 5:
        print("usage: collect_changes.py <base-dir> <work-dir> <patch-out> <names-out>", file=sys.stderr)
        return 2
    collect(*(os.path.abspath(a) for a in argv[1:5]))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
