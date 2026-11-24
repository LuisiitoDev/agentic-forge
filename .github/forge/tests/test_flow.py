#!/usr/bin/env python3
"""Decision-flow tests of the orchestrator, with the fake `gh` in tests/bin standing in for GitHub."""
import re
import unittest

from harness import OrchestratorHub, cm, planp, result

AGENT = dict(FORGE_KIND="agent", RUN_STARTED_AT="2026-11-18T09:00:00Z")
HUMAN = dict(FORGE_KIND="human", P_EVENT="issues", P_ACTION="labeled", P_SENDER="alice")
P7 = [{"number": 7}]
PLAN_FILE = [{"filename": ".forge/requests/12/plan.md"}]


class Flow(OrchestratorHub):
    def check(self, name, dispatch, want_labels, posted="", **scenario):
        """dispatch: "" = nothing dispatched, else a regex that must match the dispatch log."""
        with self.subTest(name):
            self.orchestrate(**scenario)
            d, l, p = self.state()
            if dispatch:
                self.assertRegex(d, dispatch)
            else:
                self.assertEqual(d, "")
            self.assertEqual(l, want_labels)
            if posted:
                self.assertRegex(p, posted)
            self.assertEqual(self.unhandled(), "")

    def a(self, agent, **kw):
        return dict(AGENT, AGENT=agent, CONCLUSION="success", **kw)

    def test_agent_results(self):
        c = self.check
        ba = lambda s, t="2026-11-18T10:00:00Z", u="forge[bot]": [cm(result("ba", s), t, u)]
        c("ba complete", "forge-planner-claude.yml", "forge planning", labels="forge", comments=ba("complete"), **self.a("ba"))
        c("ba clarif", "", "forge needs-clarification", labels="forge", comments=ba("needs-clarification"), **self.a("ba"))
        plan = [cm(result("planner", "ready-for-review", 2, 1), "2026-11-18T10:00:00Z")]
        c("planner ready", "", "forge spec-review", labels="forge planning ready-for-dev", pulls=P7, comments=plan, **self.a("planner"))
        c("planner no pr", "", "blocked forge", "Blocked", labels="forge planning", pulls=[],
          comments=[cm(result("planner", "ready-for-review"), "2026-11-18T10:00:00Z")], **self.a("planner"))
        c("stale", "", "blocked forge", "no result", labels="forge planning", comments=ba("complete", "2026-11-18T08:00:00Z"), **self.a("ba"))
        c("forged result", "", "blocked forge", "no result", labels="forge", comments=ba("complete", u="mallory"), **self.a("ba"))
        c("other agent heading", "", "blocked forge", "no result", labels="forge",
          comments=[cm(result("planner", "ready-for-review"), "2026-11-18T10:00:00Z")], **self.a("ba"))
        c("heading must be first", "", "blocked forge", "no result", labels="forge",
          comments=[cm("Some note first\n" + result("ba", "complete"), "2026-11-18T10:00:00Z")], **self.a("ba"))
        c("failure", "", "blocked forge", "ended with", labels="forge planning", **dict(AGENT, AGENT="coder", CONCLUSION="failure"))

        coder_plan = lambda infra: [cm(result("planner", "ready-for-review", 2, infra), "2026-11-18T08:30:00Z"),
                                    cm(result("coder", "ready-for-review"), "2026-11-18T10:00:00Z")]
        c("coder->review even if the plan listed infra", "forge-reviewer-claude.yml", "forge", labels="forge",
          pulls=P7, files=PLAN_FILE, comments=coder_plan(1), **self.a("coder"))
        self.assertNotIn("forge-infra", self.read("dispatch.log"))
        c("coder->review", "forge-reviewer-claude.yml", "forge", labels="forge", pulls=P7, files=PLAN_FILE,
          comments=coder_plan(0), **self.a("coder"))
        c("scope gate", "", "blocked forge", "src/x.cs", labels="forge", pulls=P7, files=[{"filename": "src/x.cs"}],
          comments=coder_plan(0), **self.a("coder"))
        rv = lambda s: [cm(result("reviewer", s), "2026-11-18T10:00:00Z")]
        c("reviewer pass", "", "forge needs-review", labels="forge", comments=rv("pass"), **self.a("reviewer"))
        c("replan", "forge-planner-claude.yml", "forge planning", labels="forge ready-for-dev", pulls=P7,
          comments=rv("changes-requested"), **self.a("reviewer"))
        c("reviewer odd status", "", "blocked forge", "unknown status", labels="forge",
          comments=rv("needs-clarification"), **self.a("reviewer"))

    def test_human_events(self):
        c = self.check
        planner = lambda app, infra: [cm(result("planner", "ready-for-review", app, infra), "2026-11-18T08:30:00Z")]
        spec = "forge spec-review ready-for-dev"
        c("start", "forge-ba-claude.yml", "forge", labels="forge", **HUMAN, P_LABEL="forge")
        c("start idempotent", "", "forge planning", labels="forge planning", **HUMAN, P_LABEL="forge")
        c("bot ignored", "", "forge", labels="forge", **HUMAN, P_LABEL="forge", USER_TYPE="Bot")
        c("read perm ignored", "", "forge", labels="forge", **HUMAN, P_LABEL="forge", PERM="read")
        c("approve", "forge-coder-claude.yml", "forge ready-for-dev", labels=spec, pulls=P7, comments=planner(2, 0),
          **HUMAN, P_LABEL="ready-for-dev")
        c("plan with infra tasks is refused", "", "forge spec-review", "does not run infrastructure work", labels=spec,
          pulls=P7, comments=planner(2, 1), **HUMAN, P_LABEL="ready-for-dev")
        c("plan with only infra tasks is refused", "", "forge spec-review", "a person does it", labels=spec,
          pulls=P7, comments=planner(0, 1), **HUMAN, P_LABEL="ready-for-dev")
        c("plan without application tasks", "", "blocked forge ready-for-dev", "no application tasks", labels=spec,
          pulls=P7, comments=planner(0, 0), **HUMAN, P_LABEL="ready-for-dev")
        c("not awaiting", "", "forge", "not awaiting", labels="forge ready-for-dev", **HUMAN, P_LABEL="ready-for-dev")
        c("approve no perm", "", "forge ready-for-dev spec-review", labels=spec, **HUMAN, P_LABEL="ready-for-dev", PERM="read")
        comment = dict(HUMAN, P_EVENT="issue_comment", P_ACTION="created")
        c("resume ba", "forge-ba-claude.yml", "forge", labels="forge needs-clarification",
          comments=[cm(result("ba", "needs-clarification"), "2026-11-18T08:00:00Z")], **comment)
        c("comment ignored", "", "forge planning", labels="forge planning", **comment)

    def test_results_on_the_plan_pull_request(self):
        c = self.check
        c("coder result on PR", r"forge-reviewer-claude.yml.*thread=7", "forge", labels="forge", pulls=P7, files=PLAN_FILE,
          comments=[cm(result("planner", "ready-for-review", 2, 0), "2026-11-18T08:30:00Z")],
          pr_comments=[cm(result("coder", "ready-for-review"), "2026-11-18T10:00:00Z")], **self.a("coder"))
        c("reviewer on PR", "", "forge needs-review", labels="forge", pulls=P7,
          pr_comments=[cm(result("reviewer", "pass"), "2026-11-18T10:00:00Z")], **self.a("reviewer"))
        c("forged on PR", "", "blocked forge", "no result", labels="forge", pulls=P7,
          pr_comments=[cm(result("reviewer", "pass"), "2026-11-18T10:00:00Z", "mallory")], **self.a("reviewer"))
        c("replan result on PR", "", "forge spec-review", labels="forge planning", pulls=P7,
          pr_comments=[cm(result("planner", "ready-for-review", 1, 0), "2026-11-18T10:00:00Z")], **self.a("planner"))
        c("ba thread is issue", r"forge-planner-claude.yml.*thread=12", "forge planning", labels="forge",
          comments=[cm(result("ba", "complete"), "2026-11-18T10:00:00Z")], **self.a("ba"))
        c("replan thread is PR", r"forge-planner-claude.yml.*thread=7", "forge planning", labels="forge ready-for-dev",
          pulls=P7, pr_comments=[cm(result("reviewer", "changes-requested"), "2026-11-18T10:00:00Z")], **self.a("reviewer"))
        c("coder thread is PR", r"forge-coder-claude.yml.*thread=7", "forge ready-for-dev", labels="forge spec-review ready-for-dev",
          pulls=P7, comments=[cm(result("planner", "ready-for-review", 2, 0), "2026-11-18T08:30:00Z")],
          **HUMAN, P_LABEL="ready-for-dev")

    def test_approval_policy(self):
        """ADR-0012: no self-approval, sensitive paths need admin or maintain."""
        c = self.check
        spec = "forge spec-review ready-for-dev"
        ok = [cm(planp("src/a.cs"), "2026-11-18T08:30:00Z")]
        pl = lambda *p: [cm(planp(*p), "2026-11-18T08:30:00Z")]
        H = dict(HUMAN, P_LABEL="ready-for-dev")
        base = dict(labels=spec, pulls=P7)
        c("self-approval refused", "", "forge spec-review", "You opened this request", comments=ok, REQUESTER="alice", **base, **H)
        c("self-approval allowed", "forge-coder-claude.yml", "forge ready-for-dev", comments=ok, REQUESTER="alice",
          FORGE_ALLOW_SELF_APPROVAL="true", **base, **H)
        c("self-approval case", "", "forge spec-review", "You opened this request", comments=ok, REQUESTER="ALICE", **base, **H)
        c("requester unknown", "", "forge spec-review", "Could not tell who opened", comments=ok, REQUESTER_FAIL="1", **base, **H)

        S = dict(FORGE_SENSITIVE_PATHS="infra/,.github/workflows/")
        c("sensitive plan path, write", "", "forge spec-review", "admin or maintain", comments=pl("src/a.cs", "infra/main.bicep"), **S, **base, **H)
        c("sensitive PR file, write", "", "forge spec-review", "ci.yml", comments=ok,
          files=[{"filename": ".github/workflows/ci.yml"}], **S, **base, **H)
        c("sensitive, maintain", "forge-coder-claude.yml", "forge ready-for-dev", comments=pl("infra/main.bicep"), ROLE="maintain", **S, **base, **H)
        c("sensitive, admin", "forge-coder-claude.yml", "forge ready-for-dev", comments=pl("infra/main.bicep"), PERM="admin", **S, **base, **H)
        c("not sensitive, write", "forge-coder-claude.yml", "forge ready-for-dev", comments=ok, **S, **base, **H)
        c("segment boundary", "forge-coder-claude.yml", "forge ready-for-dev", comments=pl("infra-docs/readme.md"), **S, **base, **H)
        c("case-insensitive", "", "forge spec-review", "admin or maintain", comments=pl("INFRA/main.bicep"), **S, **base, **H)
        c("traversal", "", "forge spec-review", "admin or maintain", comments=pl("src/../infra/x.bicep"), **S, **base, **H)
        c("role unknown", "", "forge spec-review", "unknown", comments=pl("infra/main.bicep"), ROLE_FAIL="1", **S, **base, **H)
        c("files unknown", "", "forge spec-review", "Could not list", comments=ok, FILES_FAIL="1", **S, **base, **H)
        c("list parsing", "", "forge spec-review", "admin or maintain", comments=pl("infra/x.bicep"),
          FORGE_SENSITIVE_PATHS=" docs/ , infra/ ", **base, **H)
        c("no config", "forge-coder-claude.yml", "forge ready-for-dev", comments=pl("infra/x.bicep"),
          FORGE_SENSITIVE_PATHS="", **base, **H)

    def test_sensitive_paths_after_implementation(self):
        """The Coder's real changes are checked against the approved plan's sensitive paths before review."""
        c = self.check
        S = dict(FORGE_SENSITIVE_PATHS="src/auth/", labels="forge", pulls=P7, **self.a("coder"))
        cfg = lambda: self.write("contents/.forge/config.yml", '{"content": "%s"}' % __import__("base64").b64encode(
            b"paths:\n  app:\n    - src\n").decode())
        done = lambda *p: [cm(planp(*p), "2026-11-18T08:30:00Z"), cm(result("coder", "ready-for-review"), "2026-11-18T10:00:00Z")]
        c("undeclared sensitive file", "", "blocked forge", "(?s)did not declare.*src/auth/login.cs", setup=cfg,
          files=[{"filename": "src/a.cs"}, {"filename": "src/auth/login.cs"}], comments=done("src/a.cs"), **S)
        c("declared sensitive file", "forge-reviewer-claude.yml", "forge", setup=cfg,
          files=[{"filename": "src/auth/login.cs"}], comments=done("src/auth/"), **S)
        c("plan lookup fails", "", "blocked forge", "Could not compare", setup=cfg, files=[{"filename": "src/a.cs"}],
          comments=done("src/a.cs"), COMMENTS_FAIL_AFTER="1", **S)
        c("unreadable plan paths", "", "blocked forge", "Could not compare", setup=cfg,
          files=[{"filename": "src/a.cs"}],
          comments=[cm(planp().replace("paths:\n", "paths: src\n"), "2026-11-18T08:30:00Z"),
                    cm(result("coder", "ready-for-review"), "2026-11-18T10:00:00Z")], **S)

    def test_claude_dispatch_shapes(self):
        """ADR-0024: every agent is dispatched as forge-<agent>-claude.yml with the inputs it takes."""
        c = self.check
        c("ba is forge-ba-claude.yml", "forge-ba-claude.yml", "forge", labels="forge", **HUMAN, P_LABEL="forge")
        c("planner is forge-planner-claude.yml", "forge-planner-claude.yml", "forge planning", labels="forge",
          comments=[cm(result("ba", "complete"), "2026-11-18T10:00:00Z")], **self.a("ba"))
        c("a replan takes the plan pull request and branch", r"forge-planner-claude.yml.*pr=7.*ref=forge/issue-12",
          "forge planning", labels="forge", pulls=P7,
          pr_comments=[cm(result("reviewer", "changes-requested"), "2026-11-18T10:00:00Z")], **self.a("reviewer"))
        c("reviewer is forge-reviewer-claude.yml", r"forge-reviewer-claude.yml.*thread=7", "forge", labels="forge", pulls=P7,
          comments=[cm(result("planner", "ready-for-review", 2, 0), "2026-11-18T08:30:00Z")],
          pr_comments=[cm(result("coder", "ready-for-review"), "2026-11-18T10:00:00Z")], files=[], **self.a("coder"))
        self.assertNotIn(".lock.yml", self.read("dispatch.log"))


if __name__ == "__main__":
    unittest.main()
