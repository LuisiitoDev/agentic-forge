#!/usr/bin/env python3
"""Intake decision logic (docs/intake-spec.md, ADR-0015). Run by the intake workflow; testable without GitHub.

It turns one issue event of a target into one repository_dispatch payload for the engine, or into nothing.
It is an early filter, never the security boundary: the orchestrator re-checks the allowlist, the sender and
the approval policy. It never reads issue titles, bodies or comment text, and never echoes its inputs.

Inputs (environment), see the spec for their GitHub sources:
  EVENT_NAME ACTION REPO SENDER SENDER_TYPE ISSUE IS_PR LABEL ISSUE_LABELS BOT_LOGIN ALLOWED_TARGETS DELIVERY
send also needs: ENGINE_REPO DISPATCH_TOKEN

  decide(env)  returns one line: "dispatch <payload json>" or "ignore <reason>". Never raises.
  send(payload, env)  dispatches the payload to ENGINE_REPO. Raises RuntimeError on any failure.

Command line (the workflow): ALLOWED_TARGETS is read from targets.txt next to this script, ISSUE_LABELS from
LABELS_JSON (a JSON list of label names). Prints the decision and dispatches when it is a dispatch.
"""
import json
import os
import re
import subprocess
import sys

# The labels a person adds to act on a request: start it, approve its plan, say the questions are answered.
LABELS = ("forge", "ready-for-dev", "clarified")

def _lc(s: str) -> str:
    return s.lower()


def decide(env) -> str:
    g = lambda k: env.get(k, "")
    ev = f"{g('EVENT_NAME')}/{g('ACTION')}"
    # Labels are the only trigger (ADR-0029): a comment never starts anything, so the target runs no workflow for it.
    if ev != "issues/labeled":
        return "ignore event not handled"

    # Loop prevention: nothing the engine's own bot does may start a stage.
    sender, bot = _lc(g("SENDER")), _lc(g("BOT_LOGIN"))
    if g("SENDER_TYPE") == "Bot" or sender.endswith("[bot]") or (bot and sender == bot):
        return "ignore sender is a bot"

    # Allowlist, whole-value and case-insensitive. An empty list fails closed.
    repo = g("REPO")
    entries = [re.sub(r"\s+", "", t) for t in re.split(r"[,\n]", g("ALLOWED_TARGETS"))]
    entries = [t for t in entries if t]
    if not entries:
        return "ignore allowlist is empty"
    if not re.fullmatch(r"[A-Za-z0-9._-]+/[A-Za-z0-9._-]+", repo):
        return "ignore malformed target"
    if _lc(repo) not in [_lc(t) for t in entries]:
        return "ignore target is not on the allowlist"

    if not re.fullmatch(r"[A-Za-z0-9-]+(\[bot\])?", g("SENDER")):
        return "ignore malformed sender"
    if not re.fullmatch(r"[1-9][0-9]*", g("ISSUE")):
        return "ignore malformed issue number"
    if g("IS_PR") == "true":
        return "ignore pull request"

    label = g("LABEL")
    if label not in LABELS:
        return "ignore label not handled"
    # `clarified` counts only on a request: the label `forge` itself, matched by whole line.
    if label == "clarified" and "forge" not in g("ISSUE_LABELS").split("\n"):
        return "ignore label is not on a request"

    # Exactly the contract of the dispatch payload.
    payload = {"event": g("EVENT_NAME"), "action": g("ACTION"), "target": repo, "issue": int(g("ISSUE")),
               "sender": g("SENDER"), "delivery": g("DELIVERY"), "label": label}
    return "dispatch " + json.dumps(payload, separators=(",", ":"))


def send(payload: str, env) -> None:
    token = env.get("DISPATCH_TOKEN", "")
    if not token:
        raise RuntimeError("no dispatch token")
    body = json.dumps({"event_type": "forge", "client_payload": json.loads(payload)}, separators=(",", ":"))
    # The token is set explicitly, so a missing one never falls back to the ambient GH_TOKEN.
    r = subprocess.run(["gh", "api", "-X", "POST", f"repos/{env['ENGINE_REPO']}/dispatches", "--input", "-"],
                       input=body, text=True, capture_output=True, env=dict(env, GH_TOKEN=token))
    if r.returncode != 0:
        raise RuntimeError("dispatch failed")


def main() -> int:
    env = dict(os.environ)
    env["BOT_LOGIN"] = ""  # the App's bot is always type Bot, which decide already ignores
    env["ISSUE_LABELS"] = "\n".join(json.loads(env.get("LABELS_JSON") or "[]"))
    targets = os.path.join(os.path.dirname(os.path.abspath(__file__)), "targets.txt")
    with open(targets) as f:
        env["ALLOWED_TARGETS"] = "".join(line for line in f if not line.lstrip().startswith("#"))
    result = decide(env)
    print(result)
    if result.split(" ", 1)[0] == "dispatch":
        try:
            send(result[len("dispatch "):], env)
        except RuntimeError as e:
            print(str(e), file=sys.stderr)
            return 1
        print("dispatched")
    return 0


if __name__ == "__main__":
    sys.exit(main())
