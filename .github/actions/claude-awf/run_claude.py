#!/usr/bin/env python3
"""Runs Claude Code inside AWF (the Agentic Workflow Firewall) and writes its final message to a file.

Called by the composite action `claude-awf`. Everything comes from the environment (the action sets it from its
inputs), nothing is interpolated into a shell:
  CLAUDE_CODE_OAUTH_TOKEN  the subscription token. It reaches the container through a private env file, never a
                           command line, and the file is deleted afterwards
  AGENT_GH_TOKEN           optional read-only GitHub token, given to the agent as GH_TOKEN (the Reviewer)
  PROMPT_FILE, ALLOWED_TOOLS, DISALLOWED_TOOLS, ALLOW_DOMAINS, MAX_TURNS, OUTPUT_FILE
  RUNNER_TEMP, RUNNER_TOOL_CACHE, GITHUB_WORKSPACE   set by GitHub Actions
"""
import json
import os
import re
import shlex
import shutil
import subprocess
import sys

SECRET_VARS = ("CLAUDE_CODE_OAUTH_TOKEN", "AGENT_GH_TOKEN")
DOMAINS = re.compile(r"[A-Za-z0-9.,-]+")
# Squid access.log lines come in two shapes; both carry `<host>:443 ... CONNECT <code>`, and denied ones say TCP_DENIED.
# Only used to count: the host is never printed.
HOST_LINE = re.compile(r"[A-Za-z0-9.-]+:443\b.*?\bCONNECT \d{3}\b")


def fail(message: str) -> int:
    print(f"::error::{message}")
    return 1


def runner_script(claude_bin: str, env) -> str:
    """The command the container runs, every value quoted. It reads the prompt from a file in the workspace."""
    args = ["--print", "--output-format", "json", "--max-turns", env["MAX_TURNS"],
            "--allowed-tools", env["ALLOWED_TOOLS"], "--disallowed-tools", env["DISALLOWED_TOOLS"]]
    return "#!/usr/bin/env bash\nexec " + " ".join(shlex.quote(x) for x in [claude_bin, *args]) \
        + " < " + shlex.quote(env["PROMPT_FILE"]) + "\n"


def env_file_text(env) -> str:
    lines = [f"CLAUDE_CODE_OAUTH_TOKEN={env['CLAUDE_CODE_OAUTH_TOKEN']}", "DISABLE_TELEMETRY=1",
             "DISABLE_ERROR_REPORTING=1", "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1"]
    if env.get("AGENT_GH_TOKEN"):
        lines.append(f"GH_TOKEN={env['AGENT_GH_TOKEN']}")
    return "\n".join(lines) + "\n"


def result_line(stdout: str):
    """awf prints its own banner on stdout: the last JSON object of type `result` is what claude wrote."""
    found = None
    for line in stdout.splitlines():
        if not line.startswith("{"):
            continue
        try:
            obj = json.loads(line)
        except ValueError:
            continue
        if isinstance(obj, dict) and obj.get("type") == "result":
            found = obj
    return found


def firewall_summary(access_log: str) -> str:
    """Counts only: how many connections the firewall allowed and denied. No host name or address is printed, so the
    job log shows nothing about what the agent talked to."""
    try:
        with open(access_log, encoding="utf-8", errors="replace") as f:
            lines = f.read().splitlines()
    except OSError:
        return ""
    allowed = denied = 0
    for line in lines:
        if not HOST_LINE.search(line):
            continue
        if "TCP_DENIED" in line:
            denied += 1
        else:
            allowed += 1
    return f"{allowed} connections allowed, {denied} denied"


def main(env=os.environ) -> int:
    for name in ("CLAUDE_CODE_OAUTH_TOKEN", "PROMPT_FILE", "ALLOWED_TOOLS", "ALLOW_DOMAINS", "MAX_TURNS", "OUTPUT_FILE",
                 "RUNNER_TEMP", "GITHUB_WORKSPACE"):
        if not env.get(name):
            return fail(f"{name} is not set")
    env = dict(env)
    env.setdefault("DISALLOWED_TOOLS", "WebFetch,WebSearch")
    if not re.fullmatch(r"[0-9]+", env["MAX_TURNS"]):
        return fail("bad max-turns")
    if not DOMAINS.fullmatch(env["ALLOW_DOMAINS"]):
        return fail("bad allow-domains")
    claude_bin, node_bin = shutil.which("claude"), shutil.which("node")
    if not claude_bin or not node_bin:
        return fail("claude and node must be installed")

    temp = env["RUNNER_TEMP"]
    env_path = f"{temp}/awf.env"
    run_dir = ".claude-run"
    logs = f"{temp}/awf-logs"
    os.makedirs(run_dir, exist_ok=True)
    os.makedirs(logs, exist_ok=True)
    # The token goes to the container through a private file (mode 0600), never on a command line.
    fd = os.open(env_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        f.write(env_file_text(env))
    script = f"{run_dir}/run.sh"
    with open(script, "w") as f:
        f.write(runner_script(claude_bin, env))
    os.chmod(script, 0o700)

    # awf itself must not inherit the secrets: the container gets them from the env file only.
    child_env = {k: v for k, v in env.items() if k not in SECRET_VARS}
    cmd = ["sudo", "-E", "env", f"PATH={env.get('PATH', '')}", "awf",
           "--allow-domains", env["ALLOW_DOMAINS"],
           "--container-workdir", env["GITHUB_WORKSPACE"],
           "--mount", f"{env['RUNNER_TOOL_CACHE']}:{env['RUNNER_TOOL_CACHE']}:ro",
           "--env-file", env_path,
           "--env", f"PATH={os.path.dirname(node_bin)}:/usr/local/bin:/usr/bin:/bin",
           "--proxy-logs-dir", logs, "--log-level", "info",
           "--", "bash", script]
    try:
        proc = subprocess.run(cmd, env=child_env, capture_output=True, text=True, errors="replace")
    finally:
        for p in (env_path, script):
            try:
                os.remove(p)
            except OSError:
                pass
    with open(f"{temp}/claude.out", "w", encoding="utf-8") as f:
        f.write(proc.stdout)
    with open(f"{temp}/claude.err", "w", encoding="utf-8") as f:
        f.write(proc.stderr)
    print(f"awf exit code: {proc.returncode}")
    print("--- firewall: " + firewall_summary(f"{logs}/access.log"))

    result = result_line(proc.stdout)
    if result is None:
        print("\n".join(proc.stderr.splitlines()[-20:]))
        return fail("Claude produced no result line")
    print(json.dumps({k: result.get(k) for k in ("is_error", "subtype", "api_error_status")}, indent=2))
    if result.get("is_error") is not False:
        return fail("Claude reported an error")
    text = result.get("result") or ""
    if not text.strip():
        return fail("Claude's result is empty")
    with open(env["OUTPUT_FILE"], "w", encoding="utf-8") as f:
        f.write(text)
    return 0 if proc.returncode == 0 else proc.returncode


if __name__ == "__main__":
    sys.exit(main())
