#!/usr/bin/env python3
"""Static checks on the workflows, the caller template and the repository layout (no GitHub). They guard what the
specs and the ADRs require."""
import glob
import json
import os
import re
import subprocess
import unittest

import yaml

from harness import ROOT

W = f"{ROOT}/.github/workflows"
CALLER = f"{ROOT}/.github/forge/templates/forge-caller.yml"
ACTION = f"{ROOT}/.github/actions/claude-awf/action.yml"
SHA_PIN = re.compile(r"@[0-9a-f]{40}( |$)")
EXPR = "$" + "{{"  # an expression inside a `run` block is a script-injection risk


def load(path):
    with open(path) as f:
        return yaml.safe_load(f)


def triggers(d):
    return d[True]  # PyYAML reads the key `on` as True


def steps(d):
    return [s for j in d.get("jobs", {}).values() for s in j["steps"]]


def read(path):
    with open(path) as f:
        return f.read()


def count(pattern, text):
    return len(re.findall(pattern, text))


class Intake(unittest.TestCase):
    def setUp(self):
        self.w = load(f"{W}/forge-intake.yml")
        self.c = load(CALLER)

    def test_intake_workflow(self):
        self.assertEqual(list(triggers(self.w)), ["workflow_call"])
        self.assertEqual(self.w["permissions"], {})
        self.assertEqual([s for s in steps(self.w) if "run" in s and EXPR in s["run"]], [])
        self.assertTrue(all(SHA_PIN.search(s["uses"]) for s in steps(self.w) if "uses" in s))
        self.assertIs([s["with"]["persist-credentials"] for s in steps(self.w) if "uses" in s][0], False)
        self.assertEqual([s["run"] for s in steps(self.w) if "run" in s], ["python3 engine/.github/forge/intake.py"])

    def test_caller_template(self):
        self.assertEqual(self.c["permissions"], {})
        self.assertEqual(sorted(triggers(self.c)), ["issue_comment", "issues"])
        self.assertFalse(any("steps" in j or "run" in j for j in self.c["jobs"].values()))
        job = self.c["jobs"]["forge"]
        self.assertEqual(job["uses"].split("@")[0], "LuisiitoDev/agentic-forge/.github/workflows/forge-intake.yml")
        self.assertIn("engine-ref", job["with"])

    def test_targets_entries_are_exact(self):
        """No wildcard, no organization alone, no spaces. Empty is allowed and means nothing is forwarded."""
        for line in read(f"{ROOT}/.github/forge/targets.txt").split("\n"):
            if line.strip() and not line.lstrip().startswith("#"):
                self.assertRegex(line, r"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$")


class Orchestrator(unittest.TestCase):
    """ADR-0018: human events only, and it waits for the agents it starts."""

    def setUp(self):
        self.path = f"{W}/forge-orchestrator.yml"
        self.o = load(self.path)
        self.job = self.o["jobs"]["route"]

    def test_triggers_and_wait(self):
        self.assertEqual(sorted(triggers(self.o)), ["repository_dispatch"])
        self.assertEqual(str(self.job["steps"][-1]["env"]["FORGE_AWAIT"]), "1")
        self.assertGreater(self.job["timeout-minutes"] * 60, 3300)
        self.assertEqual(self.job["permissions"]["actions"], "write")
        self.assertIs(self.job["concurrency"]["cancel-in-progress"], False)
        self.assertEqual(self.job["steps"][-1]["run"], "python3 .github/forge/orchestrate.py")

    def test_no_workflow_run_outside_comments(self):
        code = [l for l in read(self.path).split("\n") if not l.lstrip().startswith("#")]
        self.assertEqual(sum("workflow_run" in l for l in code), 0)

    def test_no_infra(self):
        self.assertEqual(glob.glob(f"{ROOT}/.github/forge/agents/infra.md") + glob.glob(f"{W}/forge-infra*"), [])
        self.assertNotIn("dispatch forge-infra", read(f"{ROOT}/.github/forge/orchestrate.py"))
        self.assertNotIn("FORGE_INFRA", read(self.path))


class Preflight(unittest.TestCase):
    """Started by hand, read-only, no event value inside a `run` block, actions pinned."""

    def setUp(self):
        self.p = load(f"{W}/forge-preflight.yml")

    def test_preflight_workflow(self):
        self.assertEqual(list(triggers(self.p)), ["workflow_dispatch"])
        self.assertEqual(sorted(self.p["permissions"].items()), [("actions", "read"), ("contents", "read")])
        self.assertEqual([s for s in steps(self.p) if "run" in s and EXPR in s["run"]], [])
        self.assertTrue(all(SHA_PIN.search(s["uses"]) for s in steps(self.p) if "uses" in s))
        self.assertIs([s.get("continue-on-error") for s in steps(self.p) if s.get("id") == "app"][0], True)
        self.assertIn("python3 .github/forge/preflight.py", [s for s in steps(self.p) if "run" in s][-1]["run"])

    def test_the_secrets_are_passed_by_name(self):
        text = read(f"{W}/forge-preflight.yml")
        self.assertEqual(text.count("HAS_CLAUDE: ${{ secrets.CLAUDE_CODE_OAUTH_TOKEN != '' && '1' || '' }}"), 1)
        self.assertNotIn("COPILOT", text)


class Agents(unittest.TestCase):
    """The agents run as plain workflows (ADR-0022, ADR-0024): their GitHub access is what the App tokens allow."""

    def setUp(self):
        self.cw_path = f"{W}/forge-agent-claude.yml"
        self.cw = load(self.cw_path)
        self.ca = load(ACTION)

    def minted(self, steps_):
        return [s for s in steps_ if "create-github-app-token" in s.get("uses", "")]

    def test_every_app_token_is_scoped_to_the_target_repository(self):
        s = self.minted(steps(self.cw))
        self.assertEqual(len(s), 2)
        self.assertTrue(all(x["with"].get("repositories") == "${{ inputs.target_repo_name }}" for x in s))
        self.assertNotIn("permission-administration", read(self.cw_path))

    def test_the_reviewer_alone_reads_ci_and_alerts(self):
        w = self.minted(self.cw["jobs"]["agent"]["steps"])[0]["with"]
        self.assertTrue(all("reviewer" in str(w[k]) for k in ("permission-actions", "permission-security-events",
                                                              "permission-vulnerability-alerts")))
        d = json.loads(read(f"{ROOT}/.github/forge/app-manifest.json"))["default_permissions"]
        self.assertTrue(all(d.get(k) == "read" for k in ("actions", "security_events", "vulnerability_alerts", "secret_scanning_alerts")))
        self.assertEqual(sorted(k for k, v in d.items() if v == "write"), ["contents", "issues", "pull_requests"])

    def test_only_deliver_writes(self):
        self.assertEqual(self.cw["permissions"], {})
        agent = self.cw["jobs"]["agent"]
        self.assertEqual(agent["permissions"], {"contents": "read"})
        self.assertFalse(any("write" in str(s.get("with", {})) for s in agent["steps"]))
        self.assertEqual([s["with"]["persist-credentials"] for s in agent["steps"] if "checkout" in s.get("uses", "")], [False, False])
        self.assertEqual([s["with"].get("permission-contents") for s in self.minted(self.cw["jobs"]["deliver"]["steps"])], ["write"])

    def test_the_four_thin_workflows(self):
        for a in ("ba", "planner", "coder", "reviewer"):
            d = load(f"{W}/forge-{a}-claude.yml")
            with self.subTest(a):
                self.assertEqual((d["jobs"]["run"]["uses"], d["jobs"]["run"]["with"]["agent"]), ("./.github/workflows/forge-agent-claude.yml", a))
                self.assertEqual(sorted(triggers(d)["workflow_dispatch"]["inputs"]), ["issue", "pr", "ref", "target", "target_repo_name", "thread"])
                self.assertEqual(d["run-name"], f"forge-{a}-claude " + "${{ inputs.target }}#${{ inputs.issue }}")
                self.assertEqual(d["permissions"], {"contents": "read"})

    def test_yaml_github_would_reject(self):
        """GitHub parses workflow YAML more strictly than PyYAML: no flow mapping holding free text with commas."""
        files = [self.cw_path, ACTION] + glob.glob(f"{W}/forge-*-claude.yml")
        bad = []
        for f in files:
            for line in read(f).split("\n"):
                if re.match(r"^\s*[A-Za-z_-]+:\s*\{[^}]*:", line) and "fromJSON" not in line and EXPR not in line:
                    bad.append(line)
        self.assertEqual(bad, [])

    def test_workflow_call_inputs_are_described_and_typed(self):
        inputs = triggers(self.cw)["workflow_call"]["inputs"].values()
        self.assertTrue(all(isinstance(v.get("description"), str) and v.get("type") == "string" and "required" in v for v in inputs))

    def test_scripts_the_workflow_runs_exist(self):
        found = sorted(set(re.findall(r"engine/\.github/forge/claude/[A-Za-z_]*\.py", read(self.cw_path))))
        self.assertEqual(len(found), 5)
        self.assertEqual([f for f in found if not os.path.isfile(f"{ROOT}/{f[len('engine/'):]}")], [])

    def test_no_expression_in_run_and_actions_pinned(self):
        self.assertEqual([s for s in steps(self.cw) if "run" in s and EXPR in s["run"]], [])
        self.assertEqual([s for s in self.ca["runs"]["steps"] if "run" in s and EXPR in s["run"]], [])
        bad = [s["uses"] for s in steps(self.cw) + self.ca["runs"]["steps"]
               if s.get("uses") and not s["uses"].startswith("./") and not re.search(r"@[0-9a-f]{40}$", s["uses"])]
        self.assertEqual(bad, [])

    def test_the_oauth_token_never_goes_on_a_command_line(self):
        text = read(ACTION) + read(f"{ROOT}/.github/actions/claude-awf/run_claude.py")
        self.assertEqual(count(r"--env CLAUDE_CODE_OAUTH_TOKEN|CLAUDE_CODE_OAUTH_TOKEN=\$", text), 0)

    def test_the_action_runs_a_python_script(self):
        self.assertEqual([s["run"] for s in self.ca["runs"]["steps"] if "run_claude" in s.get("run", "")],
                         ['python3 "$ACTION_PATH/run_claude.py"'])
        self.assertTrue(os.path.isfile(f"{ROOT}/.github/actions/claude-awf/run_claude.py"))


class Repository(unittest.TestCase):
    def tracked(self, pattern):
        return subprocess.check_output(["git", "ls-files", pattern], cwd=ROOT, text=True).split()

    def test_no_broken_relative_link_in_the_markdown_files(self):
        bad = []
        for f in self.tracked("*.md"):
            base = os.path.dirname(f)
            for m in re.finditer(r"\]\(([^)#\s]+)(#[^)]*)?\)", read(f"{ROOT}/{f}")):
                t = m.group(1)
                if not t.startswith(("http", "mailto:")) and not os.path.exists(os.path.normpath(os.path.join(ROOT, base, t))):
                    bad.append((f, t))
        self.assertEqual(bad, [])

    def test_no_gh_aw_layer(self):
        """ADR-0024: no source or lock file, no actions lock."""
        self.assertEqual(glob.glob(f"{W}/*.lock.yml") + glob.glob(f"{ROOT}/.github/aw"), [])
        self.assertEqual(glob.glob(f"{W}/forge-ba.md") + glob.glob(f"{W}/forge-planner.md")
                         + glob.glob(f"{W}/forge-coder.md") + glob.glob(f"{W}/forge-reviewer.md"), [])

    def test_no_copilot_in_the_engine(self):
        files = (glob.glob(f"{ROOT}/.github/forge/*.py") + glob.glob(f"{ROOT}/.github/forge/claude/*.py")
                 + glob.glob(f"{W}/*.yml") + [f for f in glob.glob(f"{ROOT}/.github/actions/*/*") if os.path.isfile(f)])
        self.assertEqual([f for f in files if "copilot" in read(f).lower()], [])

    def test_no_shell_script_is_left(self):
        self.assertEqual(self.tracked("*.sh"), [])


if __name__ == "__main__":
    unittest.main()
