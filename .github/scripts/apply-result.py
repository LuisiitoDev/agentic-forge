#!/usr/bin/env python3
"""Apply the labels and status from an agent result comment to the issue.

Reads the result the agent wrote (RESULT), then uses gh with GH_TOKEN to add and remove
labels on issue N. Only the labels in ALLOWED_LABELS can change: an agent cannot approve a
plan (ready-for-dev) or hand off to humans on its own. The Coder and Infra agents report
ready-for-review; that marks the plan PR ready for the Reviewer.
"""
import os
import re
import subprocess

ALLOWED_LABELS = {
    "analysis",
    "planning",
    "spec-review",
    "needs-clarification",
    "needs-review",
    "bug",
}

with open(os.environ["RESULT"], encoding="utf-8") as f:
    text = f.read()

issue = os.environ["N"]
branch = os.environ["BRANCH"]


def list_field(key):
    """Return the items of a flow list such as `  added: [a, b]` in the result."""
    match = re.search(rf"^\s+{key}:\s*\[([^\]]*)\]", text, re.M)
    if not match:
        return []
    items = (part.strip().strip("\"'") for part in match.group(1).split(","))
    return [item for item in items if item]


def gh(*args):
    subprocess.run(["gh", *args], check=False)


for label in list_field("added"):
    if label in ALLOWED_LABELS:
        gh("issue", "edit", issue, "--add-label", label)
    else:
        print(f"::warning::Not applying label {label!r} from the agent result")

added = set(list_field("added"))
for label in list_field("removed"):
    # A label both added and removed in one result stays: the add wins.
    if label in ALLOWED_LABELS and label not in added:
        gh("issue", "edit", issue, "--remove-label", label)

status = re.search(r"^status:\s*([\w-]+)", text, re.M)
if status and status.group(1) == "ready-for-review":
    gh("pr", "ready", branch)
