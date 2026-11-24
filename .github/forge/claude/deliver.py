#!/usr/bin/env python3
"""Delivery job of a Claude agent: validates the result and the changes, publishes them, posts the result.

The agent never writes to GitHub; this is the only place that does, with a token scoped to the target.

Env: AGENT TARGET ISSUE THREAD PR REF   what to deliver and where
     GH_TOKEN                           write token, also used by the git checkout in target/
     ART                                dir with result.md, changes.patch, names.txt (default: artifacts)
Run from the workspace: target/ is the checkout of the target (with credentials), ART is relative to it.
"""
import json
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from validate_changes import validate  # noqa: E402

TITLES = {"ba": "BA", "planner": "Planner", "coder": "Coder", "infra": "Infra", "reviewer": "Reviewer"}
WRITERS = ("planner", "coder", "infra")
MAX_RESULT_BYTES = 60000


class Refused(Exception):
    """A condition under which nothing may be published; the message is shown as the job's error."""


def log(msg: str) -> None:
    print(f"[deliver] {msg}", file=sys.stderr)


def run(*cmd: str, input_text: str = None, check: bool = True) -> str:
    r = subprocess.run(cmd, capture_output=True, text=True, input=input_text)
    if check and r.returncode != 0:
        raise RuntimeError(f"{' '.join(cmd[:3])} failed ({r.returncode}): {r.stderr.strip()}")
    return r.stdout


def git(*args: str) -> str:
    return run("git", "-C", "target", *args)


def yaml_status(result: str) -> str:
    """`status:` of the first ```yaml fence of the result."""
    inside = False
    for line in result.splitlines():
        if not inside:
            inside = line.startswith("```yaml")
            continue
        if line.startswith("```"):
            return ""
        m = re.match(r"status:\s*(.*)$", line)
        if m:
            return re.sub(r"\s+#.*$", "", m.group(1)).strip().strip('"')
    return ""


def config_paths(default: str, key: str) -> str:
    """paths.<key> of .forge/config.yml on the target's default branch (a newline list). Empty means read-only."""
    cfg = run("git", "-C", "target", "show", f"origin/{default}:.forge/config.yml", check=False)
    if not cfg.strip():
        return ""
    out = run("yq", "-r", f".paths.{key} // [] | .[]", input_text=cfg, check=False)
    # -r gives raw strings with either yq; quotes are stripped anyway.
    return "\n".join(re.sub(r'^"|"$', "", line) for line in out.splitlines())


def rejected_result(title: str, agent: str, reasons) -> str:
    bullets = "\n".join(f"- {r}" for r in reasons)
    return (f"## {title} result: blocked\n\n```yaml\nstatus: blocked\n```\n\n"
            f"The engine did not publish the changes of the {agent} agent:\n\n{bullets}\n")


def publish(agent, target, issue, pr, ref, default, patch, art) -> None:
    git("config", "user.name", "forge-agent")
    git("config", "user.email", "forge-agent@users.noreply.github.com")
    msg = f"[forge] {agent} changes for #{issue}"
    patch_abs = os.path.abspath(patch)
    if agent == "planner" and not pr:
        branch = f"forge/issue-{issue}"
        existing = run("gh", "pr", "list", "--repo", target, "--head", branch, "--state", "open",
                       "--json", "number", "--jq", ".[0].number // empty").strip()
        if existing:
            log(f"plan pull request #{existing} already exists: updating its branch (idempotent rerun)")
            git("fetch", "-q", "origin", branch)
            git("checkout", "-q", "-B", branch, f"origin/{branch}")
        else:
            git("checkout", "-q", "-B", branch, f"origin/{default}")
        git("apply", "--index", "--whitespace=nowarn", "-p2", patch_abs)
        git("commit", "-q", "-m", msg)
        git("push", "-q", "origin", f"HEAD:refs/heads/{branch}")
        if not existing:
            title = run("gh", "issue", "view", issue, "--repo", target, "--json", "title", "--jq", ".title")
            title = re.sub(r"[\r\n]", "", title)[:200]
            run("gh", "pr", "create", "--repo", target, "--draft", "--base", default, "--head", branch,
                "--title", f"[forge] {title}", "--body", f"Plan for #{issue}, prepared by the Forge planner. Closes #{issue}")
        return
    if not pr:
        raise Refused(f"A pull request is required to publish {agent} changes")
    info = json.loads(run("gh", "pr", "view", pr, "--repo", target,
                          "--json", "headRefName,title,state,isCrossRepository"))
    if info.get("headRefName") != ref:
        raise Refused(f"The pull request head is not {ref}")
    if info.get("state") != "OPEN":
        raise Refused("The pull request is not open")
    if info.get("isCrossRepository") is not False:
        raise Refused("The pull request comes from a fork")
    if not str(info.get("title", "")).startswith("[forge] "):
        raise Refused("The pull request title does not start with [forge]")
    git("fetch", "-q", "origin", ref)
    git("checkout", "-q", "-B", ref, f"origin/{ref}")
    git("apply", "--index", "--whitespace=nowarn", "-p2", patch_abs)
    git("commit", "-q", "-m", msg)
    git("push", "-q", "origin", f"HEAD:refs/heads/{ref}")


def deliver(env) -> None:
    agent, target = env["AGENT"], env["TARGET"]
    issue, thread = env["ISSUE"], env["THREAD"]
    pr, ref = env.get("PR", ""), env.get("REF", "")
    art = env.get("ART", "artifacts")
    if agent not in TITLES:
        raise Refused("unknown agent")
    if not (re.fullmatch(r"[A-Za-z0-9._-]+/[A-Za-z0-9._-]+", target) and re.fullmatch(r"[0-9]+", issue)
            and re.fullmatch(r"[0-9]+", thread)) or (pr and not re.fullmatch(r"[0-9]+", pr)):
        raise Refused("bad inputs")
    title = TITLES[agent]
    result_path = f"{art}/result.md"
    with open(result_path, encoding="utf-8") as f:
        result = f.read()
    if not re.fullmatch(rf"## {title} result: [a-z-]+", result.split("\n", 1)[0]):
        raise Refused(f"The result does not start with '## {title} result: <status>'")
    if len(result.encode("utf-8")) > MAX_RESULT_BYTES:
        raise Refused("The result is too long")
    result = re.sub(r"<!--.*?-->", "", result, flags=re.S)  # HTML comments are removed when published
    status = yaml_status(result)
    log(f"{agent} status: {status or 'unknown'}")

    patch = f"{art}/changes.patch"
    has_patch = os.path.exists(patch) and os.path.getsize(patch) > 0
    default = None
    if agent in WRITERS and has_patch and status != "blocked":
        default = run("gh", "repo", "view", target, "--json", "defaultBranchRef",
                      "--jq", ".defaultBranchRef.name").strip()
        allowed = config_paths(default, "infra" if agent == "infra" else "app") if agent in ("coder", "infra") else ""
        with open(f"{art}/names.txt", encoding="utf-8", errors="surrogateescape") as f:
            names = f.read().splitlines()
        with open(patch, "rb") as f:
            patch_bytes = f.read()
        reasons = validate(agent, issue, names, allowed, patch_bytes)
        if reasons:
            log("changes rejected")
            result = rejected_result(title, agent, reasons)
        else:
            publish(agent, target, issue, pr, ref, default, patch, art)
    with open(result_path, "w", encoding="utf-8") as f:
        f.write(result)
    run("gh", "issue", "comment", thread, "--repo", target, "--body-file", result_path)


def main() -> int:
    try:
        deliver(os.environ)
    except Refused as e:
        print(f"::error::{e}")
        return 1
    except (RuntimeError, KeyError, OSError) as e:
        print(f"::error::{e}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
