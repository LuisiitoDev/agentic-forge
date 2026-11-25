#!/usr/bin/env python3
"""Tests of the pre-flight check (docs/first-run-checklist.md), with the fake `gh`. It reads configuration and prints a
table; it changes nothing."""
import base64
import json
import os
import re
import subprocess
import sys
import unittest

from harness import FORGE, FakeHub

SHA = "0123456789abcdef0123456789abcdef01234567"
GOOD_WF = {"workflows": [{"name": n, "state": "active"} for n in (
    "forge-ba-claude", "forge-planner-claude", "forge-coder-claude", "forge-reviewer-claude", "forge-orchestrator", "forge-intake")]}


def caller(pin, engine_ref=None):
    return ("name: forge\non:\n  issues:\n    types: [labeled]\njobs:\n  forge:\n"
            f"    uses: eng/forge/.github/workflows/forge-intake.yml@{pin}\n    with:\n      engine-ref: {engine_ref or pin}\n"
            "    secrets:\n      dispatch-token: ${{ secrets.FORGE_DISPATCH_TOKEN }}\n")


class Preflight(FakeHub):
    def file_json(self, name, text):
        self.write(name, {"content": base64.b64encode(text.encode()).decode()})

    def good(self):
        self.reset()
        os.makedirs(f"{self.dir}/contents/.forge")
        os.makedirs(f"{self.dir}/contents/.github/workflows")
        self.write("repo-labels.txt", "forge\nready-for-dev\nbug\n")
        self.file_json("contents/.forge/config.yml", "paths:\n  app: [docs/]\n")
        self.file_json("contents/.github/workflows/forge.yml", caller(SHA))
        self.file_json("engine-targets.json", "#!/usr/bin/env python3\n")
        self.write("workflows.json", GOOD_WF)

    def pf(self, **over):
        """The table, from a good setup with `over` applied. Returns (output, exit code)."""
        env = self.env(**{"TARGET": "o/r", "ENGINE_REPO": "eng/forge", "ENGINE_TOKEN": "e", "TARGET_TOKEN": "t", "ACTOR": "alice",
                          "APP_SLUG": "forge", "ALLOWED": "o/r", "BOT_LOGIN": "forge[bot]", "APP_ID": "123", "HAS_APP_KEY": "1",
                          "HAS_CLAUDE": "1", **over})
        r = self.script(os.path.join(FORGE, "preflight.py"), env)
        return r.stdout + r.stderr, r.returncode

    def expect(self, name, want, setup=None, **over):
        self.good()
        if setup:
            setup()
        out, _ = self.pf(**over)
        with self.subTest(name):
            self.assertRegex(out, want, out)
            self.assertEqual(self.unhandled(), "")

    def test_everything_in_order(self):
        self.good()
        out, rc = self.pf()
        self.assertIn("PASS", out)
        self.assertNotIn("FAIL", out, out)
        self.assertEqual(rc, 0)

    def test_engine_configuration(self):
        self.expect("no app key", "FAIL.*FORGE_APP_KEY", HAS_APP_KEY="")
        self.expect("no claude token", "FAIL.*CLAUDE_CODE_OAUTH_TOKEN", HAS_CLAUDE="")
        self.expect("claude token ok", "PASS.*CLAUDE_CODE_OAUTH_TOKEN")
        self.expect("engine row", "INFO.*Claude Code runs every agent")
        self.expect("no app id", "FAIL.*FORGE_APP_ID", APP_ID="")
        self.expect("no bot login", "FAIL.*FORGE_BOT_LOGIN", BOT_LOGIN="")
        self.good()
        _, rc = self.pf(HAS_CLAUDE="")
        self.assertNotEqual(rc, 0, "a missing Claude token must fail the preflight")
        out, _ = self.pf()
        self.assertNotIn("copilot", out.lower())

    def test_the_one_allowlist(self):
        self.expect("not in variable", "FAIL.*FORGE_ALLOWED_TARGETS", ALLOWED="other/x")
        self.expect("case insensitive", "PASS.*FORGE_ALLOWED_TARGETS", ALLOWED="O/R")
        out, _ = self.pf()
        self.assertNotIn("targets.txt", out)

    def test_the_app_token(self):
        self.expect("no token", "FAIL.*App token", TARGET_TOKEN="")
        self.expect("bot login differs", "FAIL.*bot login", APP_SLUG="other")
        self.expect("target unreadable", "FAIL.*target repository", REPO_FAIL="1")
        self.expect("permission lookup", "PASS.*permission lookup", PERM="write")

    def test_labels_and_no_config(self):
        self.expect("labels missing is only info", "INFO.*labels", setup=lambda: self.write("repo-labels.txt", "bug\n"))
        # ADR-0030: no target configuration is read, so a missing one fails nothing.
        self.expect("no config needed", "PASS.*caller", setup=lambda: os.remove(f"{self.dir}/contents/.forge/config.yml"))

    def test_the_caller(self):
        wf = "contents/.github/workflows/forge.yml"
        self.expect("no caller", "FAIL.*caller", setup=lambda: os.remove(f"{self.dir}/{wf}"))
        self.expect("placeholder pin", "FAIL.*caller", setup=lambda: self.file_json(wf, caller("<TAG-OR-SHA>")))
        self.expect("pins differ", "FAIL.*caller", setup=lambda: self.file_json(wf, caller(SHA, "main")))
        self.expect("tag pin warns", "WARN.*caller", setup=lambda: self.file_json(wf, caller("v1")))
        self.expect("pinned to a SHA", "PASS.*caller workflow.*full commit SHA")

    def test_the_pinned_ref_must_exist_in_the_engine(self):
        self.expect("intake found at the ref", "PASS.*pinned ref")
        self.expect("ref not in the engine", "FAIL.*pinned", setup=lambda: os.remove(f"{self.dir}/engine-targets.json"))

    def test_engine_workflows(self):
        self.expect("workflows missing", "FAIL.*engine workflows",
                    setup=lambda: self.write("workflows.json", {"workflows": [{"name": "forge-ba-claude", "state": "active"}]}))

    def test_nothing_is_changed(self):
        self.good()
        self.pf()
        self.assertEqual(self.read("dispatch.log"), "")
        self.assertIsNone(re.search(r"-X (POST|PUT|PATCH|DELETE)", self.read("calls.log")), self.read("calls.log"))

    def test_a_malformed_target_fails(self):
        self.good()
        out, rc = self.pf(TARGET="not a repo")
        self.assertIn("FAIL", out)
        self.assertEqual(rc, 1)


if __name__ == "__main__":
    unittest.main()
