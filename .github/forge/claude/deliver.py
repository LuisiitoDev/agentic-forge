#!/usr/bin/env python3
"""Delivery job of a Claude agent: validates the result and the changes, publishes them, posts the result.

The agent never writes to GitHub; this is the only place that does, with a token scoped to the target.

Env: AGENT TARGET ISSUE THREAD PR REF   what to deliver and where
     GH_TOKEN                           write token, also used by the git checkout in target/
     FORGE_BOT_LOGIN                    the App's bot: only its Planner result is the approved plan (Coder)
     ART                                dir with result.md, changes.patch, names.txt (default: artifacts)
Run from the workspace: target/ is the checkout of the target (with credentials), ART is relative to it.
"""
import importlib.util
import json
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from guard import find_secrets, neutralize_result, risky_files  # noqa: E402
from validate_changes import validate  # noqa: E402

# The orchestrator's reader of the agents' YAML, so the scope here is read exactly as the approval read it.
_spec = importlib.util.spec_from_file_location(
    "orchestrate", os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "orchestrate.py"))
_orch = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_orch)

TITLES = {"ba": "BA", "planner": "Planner", "coder": "Coder", "reviewer": "Reviewer"}
WRITERS = ("planner", "coder")
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


def comments_of(target: str, number: str) -> list:
    out = run("gh", "api", f"repos/{target}/issues/{number}/comments", "--paginate", "--jq", ".[]")
    dec, i, items = json.JSONDecoder(), 0, []
    while True:
        while i < len(out) and out[i].isspace():
            i += 1
        if i >= len(out):
            return items
        obj, i = dec.raw_decode(out, i)
        items.append(obj)


def plan_paths(target: str, issue: str, pr: str, bot: str) -> str:
    """The Coder's scope (ADR-0030): the `paths` of the latest Planner result the engine's bot posted, which is the
    plan a human approved. A newline list; empty when there is no such result or its list cannot be read, so the
    Coder may change nothing."""
    if not bot:
        return ""
    found = [c for n in (issue, pr) if n for c in comments_of(target, n)
             if (c.get("user") or {}).get("login") == bot and (c.get("body") or "").startswith("## Planner result:")]
    if not found:
        return ""
    found.sort(key=lambda c: c.get("created_at", ""))
    paths = _orch.yaml_list(_orch.yaml_block(found[-1]["body"]), "paths")
    return "\n".join(paths or [])


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
            # A first-pass planner ran on the default branch, so its patch creates the plan folder from nothing. The
            # new plan replaces the old one: clear the folder (the only one it may touch) before applying.
            git("rm", "-r", "-q", "--ignore-unmatch", "--", f".forge/requests/{issue}")
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
    literals = [env.get("GH_TOKEN", "")]
    leaked = find_secrets(result, literals)
    if leaked:
        # Nothing the agent produced is published, not even its text: the result itself carries the credential.
        log("the result contains a credential: not published")
        result = rejected_result(title, agent, [f"the result contained what looks like {k}" for k in leaked])
        patch_allowed = False
    else:
        patch_allowed = True
    result = neutralize_result(result)
    status = yaml_status(result)
    log(f"{agent} status: {status or 'unknown'}")

    patch = f"{art}/changes.patch"
    has_patch = os.path.exists(patch) and os.path.getsize(patch) > 0
    default = None
    if agent in WRITERS and has_patch and status != "blocked" and patch_allowed:
        default = run("gh", "repo", "view", target, "--json", "defaultBranchRef",
                      "--jq", ".defaultBranchRef.name").strip()
        allowed = plan_paths(target, issue, pr, env.get("FORGE_BOT_LOGIN", "")) if agent == "coder" else ""
        with open(f"{art}/names.txt", encoding="utf-8", errors="surrogateescape") as f:
            names = f.read().splitlines()
        with open(patch, "rb") as f:
            patch_bytes = f.read()
        reasons = validate(agent, issue, names, allowed, patch_bytes, literals)
        if reasons:
            log("changes rejected")
            result = rejected_result(title, agent, reasons)
        else:
            publish(agent, target, issue, pr, ref, default, patch, art)
            risky = risky_files(names)
            if risky:
                listing = "\n".join(f"- `{p}`" for p in risky[:30])
                result += ("\n### Files to review closely\n\nThese files change dependencies, builds, CI or scripts, "
                           f"so a change here can run code. Read them before approving:\n\n{listing}\n")
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
