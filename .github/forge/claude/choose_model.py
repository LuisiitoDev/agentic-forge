#!/usr/bin/env python3
"""Chooses the Claude model of an agent: a deterministic router, no model in the decision (ADR-0027).

Usage: choose_model.py <results.md>        (AGENT, PR, FORGE_SENSITIVE_PATHS, FORGE_MODEL_<AGENT> from the environment)
Writes model= and level= to $GITHUB_OUTPUT when set, and prints them.

Levels: haiku, sonnet, opus. Each agent starts at its base level and moves up only on a signal that is in the
engine's own data (the trusted results, the plan, the pull request), never on text the request author wrote:
  - Coder: opus when the plan lists `tasks.app` >= CODER_APP_OPUS, touches a sensitive path, or is a replan
  - Planner: opus on a replan (the Reviewer asked for changes)
  - Reviewer: opus when the plan touches a sensitive path
  - BA: haiku always
FORGE_MODEL_<AGENT> (a repository variable) pins a level. An unknown value is an error: it never falls back silently.
"""
import importlib.util
import os
import re
import sys

# One reader of the agents' YAML for the whole engine: the orchestrator's, so routing and approval read a plan alike.
_spec = importlib.util.spec_from_file_location(
    "orchestrate", os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "orchestrate.py"))
_orch = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_orch)

MODELS = {"haiku": "claude-haiku-5-5", "sonnet": "claude-sonnet-5-5", "opus": "claude-opus-5-5"}
BASE = {"ba": "haiku", "planner": "sonnet", "coder": "sonnet", "reviewer": "sonnet"}
CODER_APP_OPUS = 6          # first estimate: a plan with six or more application tasks gets the bigger model


def last_planner_section(results: str) -> str:
    parts = results.split("## Planner result:")
    return parts[-1] if len(parts) > 1 else ""


def plan_facts(results: str):
    """(application task count, paths the last Planner result lists). Both read only trusted results. Paths are None
    when the plan has a `paths` key that cannot be read."""
    block = _orch.yaml_block(last_planner_section(results))
    app = _orch.as_count(_orch.yaml_nested(block, "tasks", "app"))
    return app, _orch.yaml_list(block, "paths")


def norm(p: str) -> str:
    return p.strip().lower().lstrip("./").lstrip("/").rstrip("/")


def touches_sensitive(paths, sensitive: str) -> bool:
    prefixes = [norm(s) for s in re.split(r"[,\n]", sensitive or "") if s.strip()]
    for p in paths:
        n = norm(p)
        if "/../" in f"/{n}/" or any(n == s or n.startswith(s + "/") for s in prefixes):
            return True
    return False


def choose(agent: str, results: str, pr: str, env) -> str:
    if agent not in BASE:
        raise ValueError(f"unknown agent {agent!r}")
    pinned = (env.get(f"FORGE_MODEL_{agent.upper()}") or "").strip()
    if pinned:
        if pinned not in MODELS:
            raise ValueError(f"FORGE_MODEL_{agent.upper()} must be one of {', '.join(MODELS)}")
        return pinned
    app, paths = plan_facts(results)
    replan = bool(pr) and "## Reviewer result: changes-requested" in results
    # Unreadable paths count as sensitive: the bigger model is the safe side.
    sensitive = paths is None or touches_sensitive(paths, env.get("FORGE_SENSITIVE_PATHS", ""))
    level = BASE[agent]
    if agent == "coder" and (app >= CODER_APP_OPUS or sensitive or replan):
        level = "opus"
    if agent == "planner" and replan:
        level = "opus"
    if agent == "reviewer" and sensitive:
        level = "opus"
    return level


def main(argv, env=os.environ) -> int:
    agent = env.get("AGENT", "")
    results = ""
    if len(argv) > 1:
        try:
            with open(argv[1], encoding="utf-8") as f:
                results = f.read()
        except OSError:
            results = ""
    try:
        level = choose(agent, results, env.get("PR", ""), env)
    except ValueError as e:
        print(f"::error::{e}")
        return 1
    lines = f"level={level}\nmodel={MODELS[level]}\n"
    out = env.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a", encoding="utf-8") as f:
            f.write(lines)
    sys.stdout.write(lines)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
