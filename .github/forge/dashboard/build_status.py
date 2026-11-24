"""Build site/data/runs.json for the Forge status page.

Reads the runs of the forge-*-claude agent workflows through the `gh` CLI and groups
them by request (target#issue). No model runs here; the output only reflects GitHub Actions state.

Usage: python build_status.py <output.json>   (needs GH_TOKEN and GITHUB_REPOSITORY in the environment)
"""
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timezone

STAGES = ["ba", "planner", "coder", "reviewer"]
TITLE = re.compile(r"^forge-(ba|planner|coder|reviewer)-claude (\S+?)#(\d+)$")
FIELDS = "databaseId,displayTitle,status,conclusion,createdAt,updatedAt,url,event"


def list_runs(repo: str, limit: int = 300) -> list:
    out = subprocess.run(
        ["gh", "run", "list", "--repo", repo, "--limit", str(limit), "--json", FIELDS],
        check=True, capture_output=True, text=True,
    ).stdout
    return json.loads(out or "[]")


def stage_state(run: dict) -> str:
    if run["status"] != "completed":
        return "running" if run["status"] == "in_progress" else "queued"
    return {"success": "success", "failure": "failed", "cancelled": "cancelled"}.get(run["conclusion"], "failed")


def request_state(stages: dict) -> str:
    states = [s["state"] for s in stages.values()]
    if "failed" in states:
        return "failed"
    if "running" in states or "queued" in states:
        return "running"
    if stages.get("reviewer", {}).get("state") == "success":
        return "waiting_human"  # reviewed PR exists; a human approves and merges
    return "done" if states else "idle"


def build(runs: list) -> dict:
    requests: dict[str, dict] = {}
    for run in runs:
        m = TITLE.match(run.get("displayTitle", ""))
        if not m:
            continue
        agent, target, issue = m.group(1), m.group(2), int(m.group(3))
        key = f"{target}#{issue}"
        req = requests.setdefault(key, {"target": target, "issue": issue, "stages": {}, "updated": ""})
        # runs come newest first: keep the latest run of each stage
        if agent not in req["stages"]:
            req["stages"][agent] = {
                "state": stage_state(run),
                "run_url": run["url"],
                "started": run["createdAt"],
                "updated": run["updatedAt"],
            }
        req["updated"] = max(req["updated"], run["updatedAt"])
    for req in requests.values():
        req["stages"] = {s: req["stages"][s] for s in STAGES if s in req["stages"]}
        req["state"] = request_state(req["stages"])
    ordered = sorted(requests.values(), key=lambda r: r["updated"], reverse=True)
    return {
        "generated": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "stages": STAGES,
        "requests": ordered,
    }


def main() -> None:
    repo = os.environ.get("GITHUB_REPOSITORY", "LuisiitoDev/agentic-forge")
    data = build(list_runs(repo))
    with open(sys.argv[1], "w", encoding="utf-8") as fh:
        json.dump(data, fh, ensure_ascii=False, indent=2)
    print(f"{len(data['requests'])} requests written to {sys.argv[1]}")


if __name__ == "__main__":
    main()
