#!/usr/bin/env python3
"""What each Claude agent may use: tools, network, turns, and whether it changes files.

Usage: configure_agent.py <ba|planner|coder|infra|reviewer>      (or AGENT in the environment)
Writes tools, domains, turns and writer to $GITHUB_OUTPUT when set, and always prints them.
"""
import os
import sys

# Package registries a build or a test run needs (npm, NuGet, .NET, Python).
REGISTRIES = ("registry.npmjs.org", "registry.yarnpkg.com", "api.nuget.org", "www.nuget.org",
              "dotnetcli.azureedge.net", "builds.dotnet.microsoft.com", "pypi.org", "files.pythonhosted.org")
ANTHROPIC = ("api.anthropic.com",)
GITHUB = ("api.github.com", "github.com")   # the Reviewer reads CI results and alerts

# tools: Claude Code --allowed-tools. domains: what AWF lets through. turns: --max-turns (the agent spec's limit).
# writer: the agent's changes are collected as a patch and published by the delivery job.
AGENTS = {
    "ba":       {"tools": ("Read", "Grep", "Glob"),                          "domains": ANTHROPIC,                       "turns": 20, "writer": False},
    "planner":  {"tools": ("Read", "Grep", "Glob", "Edit", "Write", "Bash"), "domains": ANTHROPIC,                       "turns": 40, "writer": True},
    "coder":    {"tools": ("Read", "Grep", "Glob", "Edit", "Write", "Bash"), "domains": ANTHROPIC + REGISTRIES,           "turns": 60, "writer": True},
    "infra":    {"tools": ("Read", "Grep", "Glob", "Edit", "Write", "Bash"), "domains": ANTHROPIC,                       "turns": 40, "writer": True},
    "reviewer": {"tools": ("Read", "Grep", "Glob", "Bash"),                  "domains": ANTHROPIC + GITHUB + REGISTRIES, "turns": 40, "writer": False},
}


def configure(agent: str) -> dict:
    if agent not in AGENTS:
        raise ValueError(f"unknown agent {agent!r}")
    c = AGENTS[agent]
    return {"tools": ",".join(c["tools"]), "domains": ",".join(c["domains"]),
            "turns": str(c["turns"]), "writer": "true" if c["writer"] else "false"}


def main(argv) -> int:
    agent = argv[1] if len(argv) > 1 else os.environ.get("AGENT", "")
    try:
        values = configure(agent)
    except ValueError as e:
        print(f"::error::{e}")
        return 1
    lines = "".join(f"{k}={v}\n" for k, v in values.items())
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a", encoding="utf-8") as f:
            f.write(lines)
    sys.stdout.write(lines)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
