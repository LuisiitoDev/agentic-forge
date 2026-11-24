#!/usr/bin/env python3
"""Unit tests for how the orchestrator reads agent results and paths. No network, no GitHub."""
import importlib.util
import os
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("orchestrate", os.path.join(HERE, "..", "orchestrate.py"))
o = importlib.util.module_from_spec(spec)
spec.loader.exec_module(o)

PLANNER = ("## Planner result: ready-for-review\n\n```yaml\nstatus: ready-for-review   # a | b | c\n"
           "type: feature              # feature | bug\nplan_dir: .forge/requests/12/\ntasks:\n  app: 3\n  infra: 1\n"
           "paths:\n  - src/a.cs\n```\n\n### Summary\nstatus: not-this-one\n")


def res(body, at="x"):
    return {"body": body, "created_at": at}


def paths_of(body):
    return o.yaml_list(o.yaml_block(body), "paths")


class Parse(unittest.TestCase):
    def test_planner_fields(self):
        r = res(PLANNER, "2026-11-18T10:00:00Z")
        self.assertEqual(o.result_status(r), "ready-for-review")
        self.assertEqual(o.result_tasks(r, "app"), "3")
        self.assertEqual(o.result_tasks(r, "infra"), "1")
        self.assertEqual(o.result_created(r), "2026-11-18T10:00:00Z")

    def test_quoted_status_and_later_text(self):
        r = res('## BA result: complete\n```yaml\nstatus: "complete"\ntype: bug\n```\nstatus: blocked\n')
        self.assertEqual(o.result_status(r), "complete")

    def test_no_block(self):
        self.assertEqual(o.result_status(res("## BA result: complete\nno block here\nstatus: complete")), "")

    def test_no_tasks(self):
        self.assertEqual(o.result_tasks(res("```yaml\nstatus: ready-for-review\n```"), "app"), "")

    def test_scoped_parent(self):
        r = res("```yaml\nstatus: x\nother:\n  app: 9\ntasks:\n  infra: 2\n```")
        self.assertEqual(o.result_tasks(r, "app"), "")
        self.assertEqual(o.result_tasks(r, "infra"), "2")

    def test_fresh(self):
        r = res(PLANNER, "2026-11-18T10:00:00Z")
        for started, expect in (("2026-11-18T09:59:00Z", True), ("2026-11-18T10:00:00Z", True),
                                ("2026-11-18T10:00:01Z", False)):
            with self.subTest(started=started):
                orch = o.Orchestrator.__new__(o.Orchestrator)
                orch.run_started_at = started
                self.assertEqual(orch.fresh(r), expect)

    def test_lists(self):
        cases = {
            "basic": ("```yaml\nstatus: x\npaths:\n  - a/b.cs\n  - c/d\n```", ["a/b.cs", "c/d"]),
            "quotes": ("```yaml\npaths:\n  - \"a b/c\"\n  - 'd/e'\n```", ["a b/c", "d/e"]),
            "comment": ("```yaml\npaths:\n  - a/b  # why\n```", ["a/b"]),
            "ends at next key": ("```yaml\npaths:\n  - a\ntasks:\n  - not-a-path\n```", ["a"]),
            "absent": ("```yaml\nstatus: x\n```", []),
            "flow list": ("```yaml\npaths: [a, \"b c\"]  # why\n```", ["a", "b c"]),
            "empty flow list": ("```yaml\npaths: []\n```", []),
            "scalar is unreadable": ("```yaml\npaths: src/a\n```", None),
            "nested map is unreadable": ("```yaml\npaths:\n  app: src\n```", None),
        }
        for name, (body, expect) in cases.items():
            with self.subTest(name):
                self.assertEqual(paths_of(body), expect)

    def test_counts(self):
        self.assertEqual((o.as_count("3"), o.as_count(""), o.as_count("x1")), (3, 0, 0))

    def test_sensitive_hits(self):
        hits = o.sensitive_hits(["infra/a", "infra-docs/x", "./Infra/b", "src/../infra/c", "src/ok"], "infra/, db")
        self.assertEqual(hits, ["infra/a", "./Infra/b", "src/../infra/c"])

    def test_scope(self):
        bad = o.scope_violations_of([".forge/requests/12/plan.md", "src/a", "src2/b", "x"], "12", ["src", "lib/"])
        self.assertEqual(bad, ["src2/b", "x"])
        self.assertEqual(o.scope_violations_of(["a"], "12", []), ["a"])

    def test_scope_matches_validate_changes(self):
        allowed = ["./src/", "/README.md"]
        files = ["src/a", "README.md", "src/../.github/x", "SRC/b", ".forge/requests/12/../13/p.md"]
        self.assertEqual(o.scope_violations_of(files, "12", allowed),
                         ["src/../.github/x", "SRC/b", ".forge/requests/12/../13/p.md"])

    def test_unapproved_sensitive(self):
        s = "infra/, src/auth"
        self.assertEqual(o.unapproved_sensitive(["src/a.cs", "src/auth/x.cs"], ["src/a.cs"], s), ["src/auth/x.cs"])
        self.assertEqual(o.unapproved_sensitive(["src/auth/x.cs", "src/auth/y.cs"], ["src/auth/"], s), [])
        self.assertEqual(o.unapproved_sensitive(["SRC/Auth/x.cs"], ["src/auth/x.cs"], s), [])
        self.assertEqual(o.unapproved_sensitive(["src/auth/x.cs"], ["src/auth/y.cs"], s), ["src/auth/x.cs"])
        self.assertEqual(o.unapproved_sensitive(["src/a.cs"], [], s), [])


if __name__ == "__main__":
    unittest.main()
