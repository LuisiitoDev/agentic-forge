#!/usr/bin/env python3
"""Tests of run_claude.py, the script of the claude-awf action. A fake `sudo` stands in for `sudo awf ...`: it records how
awf was called (arguments, the env file and its mode, its own environment) and prints a banner and a result line."""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
SCRIPT = os.path.join(ROOT, ".github", "actions", "claude-awf", "run_claude.py")
TOKEN = "sk-ant-oat01-SECRET-TOKEN"
GH = "ghs_READONLY_TOKEN"

FAKE_SUDO = r'''#!/usr/bin/env python3
import json, os, stat, sys
D = os.environ["FAKE_DIR"]
a = sys.argv[1:]
rec = {"argv": a, "token_in_environ": any("SECRET-TOKEN" in v or "READONLY" in v for v in os.environ.values())}
ef = a[a.index("--env-file") + 1]
rec["env_file"] = open(ef).read()
rec["env_file_mode"] = stat.S_IMODE(os.stat(ef).st_mode)
script = a[-1]
rec["run_script"] = open(script).read()
json.dump(rec, open(D + "/call.json", "w"))
logs = a[a.index("--proxy-logs-dir") + 1]
open(logs + "/access.log", "w").write("1 172.30.0.20:1 api.anthropic.com:443 -:- 1.1 CONNECT 200 NONE_NONE:HIER_NONE x\n" * 2
                                       + "1 172.30.0.20:1 example.com:443 -:- 1.1 CONNECT 403 TCP_DENIED:HIER_NONE x\n")
print("[entrypoint] Agentic Workflow Firewall - Agent Container")
print("{ not json")
mode = os.environ.get("FAKE_MODE", "ok")
if mode == "ok":
    print(json.dumps({"type": "result", "subtype": "success", "is_error": False, "api_error_status": None, "result": "## BA result: complete\n\nbody"}))
elif mode == "error":
    print(json.dumps({"type": "result", "subtype": "error_during_execution", "is_error": True, "api_error_status": 401, "result": "Invalid API key"}))
elif mode == "empty":
    print(json.dumps({"type": "result", "subtype": "success", "is_error": False, "result": "  "}))
sys.exit(int(os.environ.get("FAKE_RC", "0")))
'''


class RunClaudeTests(unittest.TestCase):
    def setUp(self):
        self.d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.d, True)
        self.bin, self.ws, self.tmp, self.fake = (f"{self.d}/{n}" for n in ("bin", "ws", "tmp", "fake"))
        for p in (self.bin, self.ws, self.tmp, self.fake):
            os.makedirs(p)
        for tool in ("claude", "node"):
            with open(f"{self.bin}/{tool}", "w") as f:
                f.write("#!/bin/sh\n")
            os.chmod(f"{self.bin}/{tool}", 0o755)
        with open(f"{self.bin}/sudo", "w") as f:
            f.write(FAKE_SUDO)
        os.chmod(f"{self.bin}/sudo", 0o755)
        with open(f"{self.ws}/prompt.md", "w") as f:
            f.write("the prompt")
        self.env = {"PATH": f"{self.bin}:{os.environ['PATH']}", "FAKE_DIR": self.fake, "RUNNER_TEMP": self.tmp,
                    "RUNNER_TOOL_CACHE": f"{self.d}/toolcache", "GITHUB_WORKSPACE": self.ws,
                    "CLAUDE_CODE_OAUTH_TOKEN": TOKEN, "PROMPT_FILE": "prompt.md", "ALLOWED_TOOLS": "Read,Grep,Glob",
                    "DISALLOWED_TOOLS": "WebFetch,WebSearch", "ALLOW_DOMAINS": "api.anthropic.com", "MAX_TURNS": "20",
                    "OUTPUT_FILE": "result.md"}

    def run_it(self, **over):
        env = dict(self.env, **over)
        r = subprocess.run([sys.executable, SCRIPT], cwd=self.ws, env=env, capture_output=True, text=True)
        call = None
        if os.path.exists(f"{self.fake}/call.json"):
            with open(f"{self.fake}/call.json") as f:
                call = json.load(f)
        return r, call

    def test_a_successful_run_writes_the_final_message_and_nothing_else_leaks(self):
        r, call = self.run_it()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        with open(f"{self.ws}/result.md") as f:
            self.assertEqual(f.read(), "## BA result: complete\n\nbody")     # the banner and the bad JSON line are ignored
        self.assertIn("awf exit code: 0", r.stdout)

    def test_the_token_never_reaches_a_command_line_or_awfs_own_environment(self):
        r, call = self.run_it(AGENT_GH_TOKEN=GH)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        argv = " ".join(call["argv"])
        self.assertNotIn(TOKEN, argv)
        self.assertNotIn(GH, argv)
        self.assertFalse(call["token_in_environ"])
        self.assertNotIn(TOKEN, r.stdout + r.stderr)
        self.assertIn(f"CLAUDE_CODE_OAUTH_TOKEN={TOKEN}", call["env_file"])
        self.assertIn(f"GH_TOKEN={GH}", call["env_file"])

    def test_the_env_file_is_private_and_deleted_afterwards(self):
        _, call = self.run_it()
        self.assertEqual(call["env_file_mode"], 0o600)
        self.assertFalse(os.path.exists(f"{self.tmp}/awf.env"))
        self.assertFalse(os.path.exists(f"{self.ws}/.claude-run/run.sh"))

    def test_no_github_token_unless_one_is_given(self):
        _, call = self.run_it()
        self.assertNotIn("GH_TOKEN", call["env_file"])

    def test_the_firewall_gets_exactly_the_allowed_domains_and_the_workspace(self):
        _, call = self.run_it(ALLOW_DOMAINS="api.anthropic.com,registry.npmjs.org")
        a = call["argv"]
        self.assertEqual(a[a.index("--allow-domains") + 1], "api.anthropic.com,registry.npmjs.org")
        self.assertEqual(a[a.index("--container-workdir") + 1], self.ws)
        self.assertEqual(a[:4], ["-E", "env", a[2], "awf"])

    def test_the_command_is_quoted(self):
        _, call = self.run_it(ALLOWED_TOOLS="Read,Bash(git log:*); touch /tmp/pwned", PROMPT_FILE="my prompt.md")
        script = call["run_script"]
        self.assertIn("'Read,Bash(git log:*); touch /tmp/pwned'", script)
        self.assertIn("< 'my prompt.md'", script)
        self.assertTrue(script.startswith("#!/usr/bin/env bash\nexec "))

    def test_the_firewall_summary_counts_and_never_names_a_host(self):
        r, _ = self.run_it()
        self.assertIn("--- firewall: 2 connections allowed, 1 denied", r.stdout)
        for value in ("anthropic.com", "example.com", "172.30", ":443"):
            self.assertNotIn(value, r.stdout + r.stderr)

    def test_an_error_from_claude_fails_the_step_and_writes_no_result(self):
        r, _ = self.run_it(FAKE_MODE="error")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("Claude reported an error", r.stdout)
        self.assertFalse(os.path.exists(f"{self.ws}/result.md"))

    def test_no_result_line_or_an_empty_one_fails(self):
        self.assertNotEqual(self.run_it(FAKE_MODE="none")[0].returncode, 0)
        r, _ = self.run_it(FAKE_MODE="empty")
        self.assertNotEqual(r.returncode, 0)

    def test_a_failing_awf_fails_the_step_even_with_a_result(self):
        r, _ = self.run_it(FAKE_RC="3")
        self.assertEqual(r.returncode, 3)

    def test_bad_inputs_are_refused_before_anything_runs(self):
        for bad in ({"MAX_TURNS": "20; rm -rf /"}, {"ALLOW_DOMAINS": "a.com --env X=1"}, {"ALLOW_DOMAINS": ""},
                    {"CLAUDE_CODE_OAUTH_TOKEN": ""}, {"PROMPT_FILE": ""}):
            with self.subTest(bad=bad):
                shutil.rmtree(self.fake); os.makedirs(self.fake)
                r, call = self.run_it(**bad)
                self.assertNotEqual(r.returncode, 0)
                self.assertIsNone(call)                         # awf was never called

    def test_missing_claude_is_an_error(self):
        os.remove(f"{self.bin}/claude")
        r = subprocess.run([sys.executable, SCRIPT], cwd=self.ws, env=dict(self.env, PATH=f"{self.bin}:/nonexistent"),
                           capture_output=True, text=True)
        self.assertNotEqual(r.returncode, 0)


class ParsingTests(unittest.TestCase):
    """The two parsers, on the exact shapes seen in the real runs."""

    @classmethod
    def setUpClass(cls):
        import importlib.util
        spec = importlib.util.spec_from_file_location("run_claude", SCRIPT)
        cls.m = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.m)

    def test_result_line_reads_the_compact_json_claude_prints(self):
        out = ('[entrypoint] banner\n{"type":"system","subtype":"init"}\n'
               '{"type":"result","subtype":"success","is_error":false,"result":"## BA result: complete"}\n[done]\n')
        self.assertEqual(self.m.result_line(out)["result"], "## BA result: complete")
        self.assertIsNone(self.m.result_line("no json here\n{broken\n"))

    def test_the_last_result_wins(self):
        out = '{"type":"result","result":"first"}\n{"type":"result","result":"second"}\n'
        self.assertEqual(self.m.result_line(out)["result"], "second")

    def test_the_firewall_summary_reads_both_log_shapes(self):
        with tempfile.TemporaryDirectory() as t:
            with open(f"{t}/access.log", "w") as f:
                f.write('1795482318.219 172.30.0.20:33682 api.anthropic.com:443 -:- 1.1 CONNECT 200 NONE_NONE:HIER_NONE api.anthropic.com:443 "-"\n'
                        '1795482318.414 172.30.0.20:33676 api.anthropic.com 160.79.104.10:443 1.1 CONNECT 200 TCP_TUNNEL:HIER_DIRECT api.anthropic.com:443 "-"\n'
                        '1795482309.732 ::1:36780 - -:- 0.0 - 0 NONE_NONE:HIER_NONE error:transaction-end-before-headers "-"\n')
            summary = self.m.firewall_summary(f"{t}/access.log")
        self.assertEqual(summary, "2 connections allowed, 0 denied")     # the squid error line is not a connection
        for value in ("anthropic", "160.79", "transaction"):
            self.assertNotIn(value, summary)
        self.assertEqual(self.m.firewall_summary("/nonexistent/access.log"), "")


if __name__ == "__main__":
    unittest.main(verbosity=1)
