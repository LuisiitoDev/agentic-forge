#!/usr/bin/env python3
"""Validates the changes an agent made before anything is published.

Usage: validate_changes.py <agent> <issue> <patch>      (changed paths on stdin, one per line)
Env:   ALLOWED  coder only: newline-separated path prefixes from the target's .forge/config.yml
Exit 0 and no output when the change is acceptable; otherwise one reason per line on stdout and exit 1.
"""
import os
import re
import sys

MAX_FILES = 500
MAX_PATCH_BYTES = 5 * 1024 * 1024
# Symlinks (120000) and submodules (160000) are never accepted.
_BAD_MODE = re.compile(r"^((new file|deleted file|new|old) mode (120000|160000)|index [0-9a-f.]+ (120000|160000))$", re.M)


def norm(prefix: str) -> str:
    """The same normalization for every comparison: no ./, no leading or trailing /."""
    p = prefix.strip()
    if p.startswith("./"):
        p = p[2:]
    return p.lstrip("/").rstrip("/")


def validate(agent: str, issue: str, paths, allowed: str = "", patch_bytes: bytes = b"") -> list:
    if not re.fullmatch(r"[0-9]+", issue or ""):
        return ["bad issue"]
    reasons = []
    prefixes = [x for x in (norm(a) for a in allowed.splitlines()) if x]
    count = 0
    for p in paths:
        if not p:
            continue
        count += 1
        if p.startswith("/") or ".." in p or "\\" in p or any(ord(c) < 32 or ord(c) == 127 for c in p):
            reasons.append(f"unsafe path: {p}")
            continue
        if ".git" in p.split("/"):
            reasons.append(f"touches .git: {p}")
            continue
        if p == ".github" or p.startswith(".github/"):
            reasons.append(f"touches .github: {p}")
            continue
        if p == ".forge/config.yml":
            reasons.append(f"touches .forge/config.yml: {p}")
            continue
        if agent == "planner":
            if not p.startswith(f".forge/requests/{issue}/"):
                reasons.append(f"outside .forge/requests/{issue}/: {p}")
        elif agent == "coder":
            if not any(p == a or p.startswith(a + "/") for a in prefixes):
                reasons.append(f"outside the paths this target allows for {agent}: {p}")
        else:
            reasons.append(f"{agent} may not change files: {p}")
    if count > MAX_FILES:
        reasons.append(f"too many changed files ({count})")
    if len(patch_bytes) > MAX_PATCH_BYTES:
        reasons.append("patch larger than 5 MiB")
    if _BAD_MODE.search(patch_bytes.decode("utf-8", "replace")):
        reasons.append("the change contains a symlink or a submodule")
    return reasons


def main(argv) -> int:
    if len(argv) != 4:
        print("usage: validate_changes.py <agent> <issue> <patch>", file=sys.stderr)
        return 2
    agent, issue, patch = argv[1:4]
    try:
        patch_bytes = open(patch, "rb").read() if os.path.getsize(patch) else b""
    except OSError:
        patch_bytes = b""
    reasons = validate(agent, issue, sys.stdin.read().splitlines(), os.environ.get("ALLOWED", ""), patch_bytes)
    for r in reasons:
        print(r)
    return 1 if reasons else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
