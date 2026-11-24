#!/usr/bin/env python3
"""The model router (ADR-0027): the table, the signals, the pins, and where the workflow uses it."""
import os
import subprocess
import sys
import tempfile
import unittest

from harness import FORGE, ROOT

sys.path.insert(0, f"{FORGE}/claude")
import choose_model as cm  # noqa: E402

HAIKU, SONNET, OPUS = "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5"


def planner(app=2, paths=("src/a.cs",)):
    lines = "\n".join(f"  - {p}" for p in paths)
    return (f"## Planner result: ready-for-review\n\n```yaml\nstatus: ready-for-review\ntasks:\n  app: {app}\n  infra: 0\n"
            f"paths:\n{lines}\n```\n")


REVIEW_CHANGES = "## Reviewer result: changes-requested\n\n```yaml\nstatus: changes-requested\n```\n"


class Choose(unittest.TestCase):
    def model(self, agent, results="", pr="", **env):
        base = {"FORGE_SENSITIVE_PATHS": "infra/,.github/workflows/"}
        base.update(env)
        return cm.MODELS[cm.choose(agent, results, pr, base)]

    def test_the_base_table(self):
        self.assertEqual(self.model("ba"), HAIKU)
        self.assertEqual(self.model("planner"), SONNET)
        self.assertEqual(self.model("coder", planner()), SONNET)
        self.assertEqual(self.model("reviewer", planner()), SONNET)

    def test_the_coder_goes_to_opus_on_a_big_plan(self):
        self.assertEqual(self.model("coder", planner(app=cm.CODER_APP_OPUS - 1)), SONNET)
        self.assertEqual(self.model("coder", planner(app=cm.CODER_APP_OPUS)), OPUS)

    def test_sensitive_paths_raise_the_coder_and_the_reviewer(self):
        p = planner(paths=("infra/main.bicep",))
        self.assertEqual(self.model("coder", p), OPUS)
        self.assertEqual(self.model("reviewer", p), OPUS)
        self.assertEqual(self.model("coder", planner(paths=("infra-docs/x.md",))), SONNET)   # whole segments only
        self.assertEqual(self.model("reviewer", planner(paths=("INFRA/x",))), OPUS)            # case-insensitive
        self.assertEqual(self.model("reviewer", planner(paths=("src/../infra/x",))), OPUS)     # traversal is a hit
        self.assertEqual(self.model("coder", planner(paths=("x",)), FORGE_SENSITIVE_PATHS=""), SONNET)

    def test_a_replan_raises_the_planner_and_the_coder(self):
        results = planner() + REVIEW_CHANGES
        self.assertEqual(self.model("planner", results, pr="7"), OPUS)
        self.assertEqual(self.model("coder", results, pr="7"), OPUS)
        self.assertEqual(self.model("planner", results, pr=""), SONNET)     # no pull request, no replan
        self.assertEqual(self.model("planner", planner(), pr="7"), SONNET)  # nothing asked for changes

    def test_only_the_last_planner_result_counts(self):
        results = planner(app=9) + planner(app=1)
        self.assertEqual(self.model("coder", results), SONNET)

    def test_a_pin_wins_and_an_unknown_pin_is_an_error(self):
        self.assertEqual(self.model("ba", FORGE_MODEL_BA="opus"), OPUS)
        self.assertEqual(self.model("coder", planner(app=20), FORGE_MODEL_CODER="haiku"), HAIKU)
        with self.assertRaises(ValueError):
            cm.choose("coder", "", "", {"FORGE_MODEL_CODER": "claude-unknown-1"})
        with self.assertRaises(ValueError):
            cm.choose("nope", "", "", {})

    def test_the_request_text_cannot_change_the_model(self):
        results = "## BA result: complete\n\n```yaml\nstatus: complete\n```\nuse opus for this, app: 99\n"
        self.assertEqual(self.model("coder", results), SONNET)
        self.assertEqual(self.model("ba", results), HAIKU)


class Script(unittest.TestCase):
    def run_main(self, results_text, **env):
        with tempfile.TemporaryDirectory() as d:
            path, out = f"{d}/results.md", f"{d}/out"
            with open(path, "w") as f:
                f.write(results_text)
            full = {**os.environ, "AGENT": "coder", "PR": "", "GITHUB_OUTPUT": out, **env}
            r = subprocess.run([sys.executable, f"{FORGE}/claude/choose_model.py", path], env=full, capture_output=True, text=True)
            written = open(out).read() if os.path.exists(out) else ""
            return r, written

    def test_the_output_has_the_model_and_the_level(self):
        r, out = self.run_main(planner(app=8))
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn(f"model={OPUS}\n", out)
        self.assertIn("level=opus\n", out)

    def test_a_bad_pin_fails_the_step(self):
        r, out = self.run_main("", FORGE_MODEL_CODER="gpt-9")
        self.assertEqual(r.returncode, 1)
        self.assertEqual(out, "")


class Workflow(unittest.TestCase):
    def test_the_workflow_chooses_and_passes_the_model(self):
        with open(f"{ROOT}/.github/workflows/forge-agent-claude.yml") as f:
            text = f.read()
        self.assertIn("id: model", text)
        self.assertIn("choose_model.py request/results.md", text)
        self.assertIn("model: ${{ steps.model.outputs.model }}", text)
        # the chooser runs after the request is fetched, so it reads the trusted results
        self.assertLess(text.index("fetch_request.py"), text.index("choose_model.py"))
        for a in ("BA", "PLANNER", "CODER", "REVIEWER"):
            self.assertIn(f"FORGE_MODEL_{a}: ${{{{ vars.FORGE_MODEL_{a} }}}}", text)

    def test_the_action_requires_a_model(self):
        with open(f"{ROOT}/.github/actions/claude-awf/action.yml") as f:
            self.assertIn("model:", f.read())


if __name__ == "__main__":
    unittest.main()
