#!/usr/bin/env python3
"""Reads the request from GitHub into request/ so the agent never needs a GitHub token or the network for it.

Usage: fetch_request.py [out-dir]            (default: request)
Env:   TARGET (owner/name), ISSUE, PR (optional), FORGE_BOT_LOGIN; GH_TOKEN is read by gh (read-only token).

results.md holds only the results of earlier agents: comments by the engine's bot whose first line is
`## <Agent> result: <status>`. Everything else is untrusted data and stays in issue.json / pr.json.
"""
import json
import os
import re
import subprocess
import sys

RESULT_HEADING = re.compile(r"^## (BA|Planner|Coder|Reviewer) result: ")


def gh_json(*args: str):
    r = subprocess.run(["gh", *args], capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError(f"gh {' '.join(args[:2])} failed: {r.stderr.strip()}")
    return json.loads(r.stdout)


def trusted_results(comments, bot: str) -> str:
    mine = [c for c in comments
            if (c.get("author") or {}).get("login") == bot and RESULT_HEADING.match(c.get("body") or "")]
    mine.sort(key=lambda c: c.get("createdAt", ""))
    return "\n\n---\n\n".join(
        f"<!-- result by {c['author']['login']} at {c.get('createdAt', '')} -->\n{c['body']}" for c in mine)


def main(argv) -> int:
    out = argv[1] if len(argv) > 1 else "request"
    target, issue = os.environ["TARGET"], os.environ["ISSUE"]
    pr = os.environ.get("PR", "")
    bot = os.environ["FORGE_BOT_LOGIN"]
    if not re.fullmatch(r"[A-Za-z0-9._-]+/[A-Za-z0-9._-]+", target) or not re.fullmatch(r"[0-9]+", issue):
        print("bad TARGET or ISSUE", file=sys.stderr)
        return 1
    if pr and not re.fullmatch(r"[0-9]+", pr):
        print("bad PR", file=sys.stderr)
        return 1
    os.makedirs(out, exist_ok=True)
    issue_json = gh_json("issue", "view", issue, "--repo", target,
                         "--json", "number,title,body,labels,author,comments")
    pr_comments = []
    if pr:
        pr_json = gh_json("pr", "view", pr, "--repo", target,
                          "--json", "number,title,body,headRefName,baseRefName,files,comments,reviews")
        pr_comments = pr_json.get("comments", [])
        with open(f"{out}/pr.json", "w", encoding="utf-8") as f:
            json.dump(pr_json, f)
    with open(f"{out}/issue.json", "w", encoding="utf-8") as f:
        json.dump(issue_json, f)
    with open(f"{out}/pr-comments.json", "w", encoding="utf-8") as f:
        json.dump(pr_comments, f)
    with open(f"{out}/results.md", "w", encoding="utf-8") as f:
        f.write(trusted_results(issue_json.get("comments", []) + pr_comments, bot))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv))
    except (RuntimeError, KeyError) as e:
        print(f"::error::{e}", file=sys.stderr)
        sys.exit(1)
