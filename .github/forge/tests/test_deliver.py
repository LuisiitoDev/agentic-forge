#!/usr/bin/env python3
"""Integration test of the delivery script of the Claude agents: a local bare repository stands in for the target and a
fake gh for GitHub. Checks what is pushed, which PR is opened, what is commented, and what is refused."""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
CLAUDE = os.path.join(ROOT, ".github", "forge", "claude")
GIT_ENV = {"GIT_CONFIG_SYSTEM": "/dev/null", "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
           "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t"}


class DeliverCase(unittest.TestCase):
    def setUp(self):
        self.w = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.w, True)
        os.makedirs(f"{self.w}/bin")
        shutil.copy(f"{HERE}/bin/gh-fake", f"{self.w}/bin/gh")
        os.chmod(f"{self.w}/bin/gh", 0o755)
        self.fake = f"{self.w}/fake"
        os.makedirs(self.fake)
        self.gitconfig = f"{self.w}/gitconfig"
        with open(self.gitconfig, "w") as f:
            f.write("[init]\n\tdefaultBranch = main\n[protocol \"file\"]\n\tallow = always\n")
        self.env = dict(os.environ, **GIT_ENV, GIT_CONFIG_GLOBAL=self.gitconfig,
                        PATH=f"{self.w}/bin:{os.environ['PATH']}", FAKE_DIR=self.fake)
        self.remote, self.ws = f"{self.w}/remote.git", f"{self.w}/ws"
        self.git("init", "-q", "--bare", self.remote)
        seed = f"{self.w}/seed"
        self.git("clone", "-q", self.remote, seed)
        os.makedirs(f"{seed}/src"); os.makedirs(f"{seed}/.forge")
        with open(f"{seed}/src/a.txt", "w") as f:
            f.write("a\n")
        with open(f"{seed}/.forge/config.yml", "w") as f:
            f.write("paths:\n  app:\n    - src/\n")
        self.git("add", "-A", cwd=seed)
        self.git("commit", "-qm", "seed", cwd=seed)
        self.git("push", "-q", "origin", "main", cwd=seed)
        os.makedirs(f"{self.ws}/artifacts")
        self.git("clone", "-q", self.remote, f"{self.ws}/target")

    # ---- helpers
    def git(self, *args, cwd=None, check=True):
        return subprocess.run(["git", *args], cwd=cwd, env=self.env, check=check, capture_output=True, text=True)

    def plan_branch(self):
        t = f"{self.ws}/target"
        self.git("checkout", "-q", "-b", "forge/issue-7", cwd=t)
        os.makedirs(f"{t}/.forge/requests/7")
        with open(f"{t}/.forge/requests/7/plan.md", "w") as f:
            f.write("plan\n")
        self.git("add", "-A", cwd=t); self.git("commit", "-qm", "plan", cwd=t)
        self.git("push", "-q", "origin", "forge/issue-7", cwd=t); self.git("checkout", "-q", "main", cwd=t)

    def pr_json(self, head="forge/issue-7", title="[forge] Add a hello file", state="OPEN", cross=False):
        with open(f"{self.fake}/pr_view.json", "w") as f:
            json.dump({"headRefName": head, "title": title, "state": state, "isCrossRepository": cross}, f)

    def mkpatch(self, edit):
        """edit(work_dir) changes a copy of the target; the patch and names are collected as the workflow does."""
        base, work = f"{self.w}/base", f"{self.w}/work"
        shutil.rmtree(base, True); shutil.rmtree(work, True)
        os.makedirs(base)
        arc = subprocess.run(["git", "archive", "HEAD"], cwd=f"{self.ws}/target", capture_output=True, check=True, env=self.env).stdout
        subprocess.run(["tar", "-x", "-C", base], input=arc, check=True)
        shutil.copytree(base, work, symlinks=True)
        edit(work)
        subprocess.run([sys.executable, f"{CLAUDE}/collect_changes.py", base, work, f"{self.ws}/artifacts/changes.patch",
                        f"{self.ws}/artifacts/names.txt"], check=True)

    def result(self, title, status, raw=None):
        with open(f"{self.ws}/artifacts/result.md", "w") as f:
            f.write(raw if raw is not None else f"## {title} result: {status}\n\n```yaml\nstatus: {status}\n```\n\nBody <!-- hidden -->visible\n")

    def deliver(self, agent, pr="", ref="", thread="7"):
        env = dict(self.env, AGENT=agent, TARGET="o/r", ISSUE="7", THREAD=thread, PR=pr, REF=ref, GH_TOKEN="t", ART="artifacts")
        return subprocess.run([sys.executable, f"{CLAUDE}/deliver.py"], cwd=self.ws, env=env, capture_output=True, text=True)

    def branch_exists(self, name):
        return self.git("show-ref", "--verify", "-q", f"refs/heads/{name}", cwd=self.remote, check=False).returncode == 0

    def show(self, branch, path):
        r = self.git("show", f"{branch}:{path}", cwd=self.remote, check=False)
        return r.stdout if r.returncode == 0 else None

    def comment(self):
        p = f"{self.fake}/comment.md"
        if not os.path.exists(p):
            return None
        with open(p) as f:
            return f.read()

    def created(self):
        p = f"{self.fake}/created.log"
        if not os.path.exists(p):
            return []
        with open(p) as f:
            return f.read().splitlines()


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write(text)


class PlannerTests(DeliverCase):
    def test_first_pass_pushes_the_branch_opens_a_draft_pr_and_comments(self):
        self.mkpatch(lambda w: write(f"{w}/.forge/requests/7/plan.md", "plan\n")); self.result("Planner", "ready-for-review")
        r = self.deliver("planner"); self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(self.show("forge/issue-7", ".forge/requests/7/plan.md"), "plan\n")
        self.assertEqual(len(self.created()), 1)
        self.assertIn("--draft", self.created()[0])
        self.assertIn("--title [forge] Add a hello file", self.created()[0])
        self.assertTrue(self.comment().startswith("## Planner result: ready-for-review"))
        self.assertNotIn("hidden", self.comment())                   # HTML comments are removed

    def test_a_rerun_with_the_pr_open_opens_no_second_pr(self):
        self.plan_branch(); write(f"{self.fake}/open_pr", "9\n")
        self.mkpatch(lambda w: write(f"{w}/.forge/requests/7/tasks.md", "t\n")); self.result("Planner", "ready-for-review")
        r = self.deliver("planner"); self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(self.created(), [])
        self.assertEqual(self.show("forge/issue-7", ".forge/requests/7/tasks.md"), "t\n")

    def test_a_rerun_that_rewrites_an_existing_plan_file_replaces_the_plan(self):
        """The first-pass planner works on the default branch, so its patch adds plan.md although the plan branch
        already has one (seen on forge-sandbox#6: `already exists in index`)."""
        self.plan_branch(); write(f"{self.fake}/open_pr", "9\n")
        self.mkpatch(lambda w: write(f"{w}/.forge/requests/7/plan.md", "new plan\n")); self.result("Planner", "ready-for-review")
        r = self.deliver("planner"); self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(self.created(), [])
        self.assertEqual(self.show("forge/issue-7", ".forge/requests/7/plan.md"), "new plan\n")
        self.assertEqual(self.show("forge/issue-7", "src/a.txt"), "a\n")

    def test_outside_its_folder_nothing_is_pushed_and_a_blocked_result_says_why(self):
        self.mkpatch(lambda w: write(f"{w}/src/a.txt", "a\nx\n")); self.result("Planner", "ready-for-review")
        r = self.deliver("planner"); self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertFalse(self.branch_exists("forge/issue-7"))
        self.assertTrue(self.comment().startswith("## Planner result: blocked"))
        self.assertIn("outside .forge/requests/7/", self.comment())


class WriterTests(DeliverCase):
    def test_coder_inside_paths_app_is_pushed_to_the_plan_branch_only(self):
        self.plan_branch(); self.pr_json(); self.mkpatch(lambda w: write(f"{w}/src/a.txt", "a\nb\n")); self.result("Coder", "ready-for-review")
        r = self.deliver("coder", "9", "forge/issue-7", "9"); self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(self.show("forge/issue-7", "src/a.txt"), "a\nb\n")
        self.assertEqual(self.show("main", "src/a.txt"), "a\n")      # main is never touched
        self.assertTrue(self.comment().startswith("## Coder result: ready-for-review"))

    def test_coder_outside_paths_app_is_rejected(self):
        self.plan_branch(); self.pr_json(); self.mkpatch(lambda w: write(f"{w}/infra/main.tf", "x\n")); self.result("Coder", "ready-for-review")
        r = self.deliver("coder", "9", "forge/issue-7", "9"); self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIsNone(self.show("forge/issue-7", "infra/main.tf"))
        self.assertTrue(self.comment().startswith("## Coder result: blocked"))

    def test_a_workflow_file_is_never_pushed_even_inside_the_allowed_paths(self):
        self.plan_branch(); self.pr_json(); self.mkpatch(lambda w: write(f"{w}/.github/workflows/w.yml", "x\n")); self.result("Coder", "ready-for-review")
        self.assertEqual(self.deliver("coder", "9", "forge/issue-7", "9").returncode, 0)
        self.assertIsNone(self.show("forge/issue-7", ".github/workflows/w.yml"))
        self.assertTrue(self.comment().startswith("## Coder result: blocked"))

    def test_there_is_no_infra_agent_any_more(self):
        """ADR-0023: delivering as `infra` is refused, nothing is pushed and nothing is posted."""
        self.plan_branch(); self.pr_json(); self.mkpatch(lambda w: write(f"{w}/infra/main.tf", "x\n")); self.result("Infra", "ready-for-review")
        self.assertNotEqual(self.deliver("infra", "9", "forge/issue-7", "9").returncode, 0)
        self.assertIsNone(self.show("forge/issue-7", "infra/main.tf"))
        self.assertIsNone(self.comment())

    def test_the_pull_request_is_checked_before_anything_is_pushed(self):
        for head, title, state, cross in (("other/branch", "[forge] x", "OPEN", False), ("forge/issue-7", "no prefix", "OPEN", False),
                                          ("forge/issue-7", "[forge] x", "CLOSED", False), ("forge/issue-7", "[forge] x", "OPEN", True)):
            with self.subTest(head=head, title=title, state=state, cross=cross):
                self.tearDown(); self.setUp()
                self.plan_branch(); self.pr_json(head, title, state, cross)
                self.mkpatch(lambda w: write(f"{w}/src/a.txt", "a\nb\n")); self.result("Coder", "ready-for-review")
                r = self.deliver("coder", "9", "forge/issue-7", "9")
                self.assertNotEqual(r.returncode, 0)
                self.assertIsNone(self.comment())
                self.assertEqual(self.show("forge/issue-7", "src/a.txt"), "a\n")   # unchanged

    def test_a_pr_is_required_for_coder_changes(self):
        self.mkpatch(lambda w: write(f"{w}/src/a.txt", "a\nb\n")); self.result("Coder", "ready-for-review")
        self.assertNotEqual(self.deliver("coder").returncode, 0)

    def test_a_blocked_result_publishes_no_code(self):
        self.plan_branch(); self.pr_json(); self.mkpatch(lambda w: write(f"{w}/src/a.txt", "a\nb\n")); self.result("Coder", "blocked")
        self.assertEqual(self.deliver("coder", "9", "forge/issue-7", "9").returncode, 0)
        self.assertEqual(self.show("forge/issue-7", "src/a.txt"), "a\n")   # unchanged
        self.assertTrue(self.comment().startswith("## Coder result: blocked"))


class ReadOnlyAndHeadingTests(DeliverCase):
    def test_the_reviewer_never_publishes_code(self):
        self.plan_branch(); self.pr_json(); self.mkpatch(lambda w: write(f"{w}/src/a.txt", "a\nb\n")); self.result("Reviewer", "pass")
        self.assertEqual(self.deliver("reviewer", "9", "forge/issue-7", "9").returncode, 0)
        self.assertEqual(self.show("forge/issue-7", "src/a.txt"), "a\n")   # unchanged
        self.assertTrue(self.comment().startswith("## Reviewer result: pass"))

    def test_a_wrong_heading_is_refused_and_nothing_is_posted(self):
        self.mkpatch(lambda w: None); self.result("BA", "complete", raw="hello\n")
        self.assertNotEqual(self.deliver("ba").returncode, 0)
        self.assertIsNone(self.comment())

    def test_another_agents_heading_is_refused(self):
        self.mkpatch(lambda w: None); self.result("Coder", "complete", raw="## Coder result: complete\n")
        self.assertNotEqual(self.deliver("ba").returncode, 0)

    def test_bad_inputs_are_refused(self):
        self.mkpatch(lambda w: None); self.result("BA", "complete")
        env = dict(self.env, AGENT="ba", TARGET="o/r;x", ISSUE="7", THREAD="7", PR="", REF="", GH_TOKEN="t", ART="artifacts")
        r = subprocess.run([sys.executable, f"{CLAUDE}/deliver.py"], cwd=self.ws, env=env, capture_output=True, text=True)
        self.assertNotEqual(r.returncode, 0)

    def test_a_result_with_no_change_is_just_commented(self):
        self.mkpatch(lambda w: None); self.result("BA", "complete")
        self.assertEqual(self.deliver("ba").returncode, 0)
        self.assertTrue(self.comment().startswith("## BA result: complete"))


if __name__ == "__main__":
    unittest.main(verbosity=1)
