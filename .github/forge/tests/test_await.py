#!/usr/bin/env python3
"""Tests of the orchestrator's wait for the agent runs it dispatches (ADR-0018), with the fake `gh`.
The chain: a human event dispatches an agent, the orchestrator waits for that run, reads its result and dispatches
the next one, until a human gate or a failure ends it."""
import json
import re
import unittest

from harness import OrchestratorHub, cm, result

START = dict(FORGE_KIND="human", P_EVENT="issues", P_ACTION="labeled", P_SENDER="alice", P_LABEL="forge",
             FORGE_AWAIT="1", AWAIT_INTERVAL="0", AWAIT_FIND_TRIES="2", AWAIT_MAX="1000", FORGE_NOW="2026-11-18T08:59:50Z")
P7 = [{"number": 7}]


def title(agent):
    return f"forge-{agent}-claude o/r#12"


def run_json(rid, t, created):
    return {"id": rid, "display_title": t, "created_at": created, "event": "workflow_dispatch"}


class Await(OrchestratorHub):
    def runs_of(self, agent, runs):
        self.write(f"runs-forge-{agent}-claude.json", {"workflow_runs": runs})

    def run_info(self, rid, status, conclusion, started):
        self.write(f"run-{rid}.json", {"status": status, "conclusion": conclusion or None, "run_started_at": started})

    def ba_runs(self):
        self.runs_of("ba", [run_json(99, "forge-ba-claude o/x#12", "2026-11-18T09:00:06Z"),
                            run_json(101, title("ba"), "2026-11-18T09:00:05Z")])

    def ok_run(self, rid, started):
        self.run_info(rid, "completed", "success", started)

    def chain(self):
        self.ba_runs()
        self.ok_run(101, "2026-11-18T09:00:08Z")
        self.runs_of("planner", [run_json(102, title("planner"), "2026-11-18T09:06:00Z")])
        self.ok_run(102, "2026-11-18T09:06:03Z")

    def check(self, name, dispatched, want_labels, posted="", setup=None, **scenario):
        with self.subTest(name):
            self.orchestrate(setup=setup, **{**START, **scenario})
            d = " ".join(re.findall(r"forge-[a-z]*(?=-claude\.yml|\.yml)", self.read("dispatch.log")))
            _, l, p = self.state()
            self.assertEqual(d, dispatched, self.stderr)
            self.assertEqual(l, want_labels)
            if posted:
                self.assertIn(posted, p)
            self.assertEqual(self.unhandled(), "")

    def dispatches(self, text):
        return self.read("dispatch.log").count(text)

    def test_chain(self):
        ba_planner = [cm(result("ba", "complete"), "2026-11-18T09:05:00Z"),
                      cm(result("planner", "ready-for-review"), "2026-11-18T09:10:00Z", "forge[bot]")]
        self.check("chain ba, planner, gate", "forge-ba forge-planner", "forge spec-review", labels="forge",
                   setup=self.chain, pulls=P7, comments=ba_planner)
        self.check("ba asks, chain ends", "forge-ba", "forge needs-clarification", labels="forge",
                   setup=lambda: (self.ba_runs(), self.ok_run(101, "2026-11-18T09:00:08Z")),
                   comments=[cm(result("ba", "needs-clarification"), "2026-11-18T09:05:00Z")])
        self.check("stage limit", "forge-ba forge-planner", "blocked forge", "too many stages", labels="forge",
                   setup=self.chain, pulls=P7, FORGE_MAX_STAGES="1", comments=ba_planner)
        self.check("no wait when off", "forge-ba", "forge", labels="forge", setup=self.chain, FORGE_AWAIT="0")

    def test_every_dispatch_names_the_target_repository(self):
        self.orchestrate(labels="forge", setup=self.chain, pulls=P7, **START,
                         comments=[cm(result("ba", "complete"), "2026-11-18T09:05:00Z"),
                                   cm(result("planner", "ready-for-review"), "2026-11-18T09:10:00Z")])
        self.assertEqual(self.dispatches("-f target_repo_name=r"), 2, self.read("dispatch.log"))

    def test_runs_that_do_not_count(self):
        self.check("run never appears", "forge-ba", "blocked forge", "did not start", labels="forge", setup=lambda: None)

        def decoy():
            self.runs_of("ba", [run_json(99, "forge-ba-claude o/x#12", "2026-11-18T09:00:06Z"),
                                run_json(98, title("ba"), "2026-11-18T08:00:00Z")])
            self.ok_run(98, "2026-11-18T08:00:01Z")
            self.ok_run(99, "2026-11-18T09:00:07Z")
        self.check("decoy and stale runs ignored", "forge-ba", "blocked forge", "did not start", labels="forge",
                   setup=decoy, comments=[cm(result("ba", "complete"), "2026-11-18T09:05:00Z")])

    def test_failures(self):
        self.check("run failed", "forge-ba", "blocked forge", "ended with", labels="forge",
                   setup=lambda: (self.ba_runs(), self.run_info(101, "completed", "failure", "2026-11-18T09:00:08Z")))
        slow = lambda: (self.ba_runs(), self.run_info(101, "in_progress", "", "2026-11-18T09:00:08Z"))
        self.check("run too slow", "forge-ba", "blocked forge", "did not finish", labels="forge", setup=slow, AWAIT_MAX="0")
        # the token is about to expire and the wait cannot be handed off: blocked, while the token can still say so
        self.check("token budget, no hand-off", "forge-ba", "blocked forge", "token", labels="forge", setup=slow,
                   FORGE_TOKEN_BUDGET="0", HANDOFF_FAIL="1")
        self.check("stale result", "forge-ba", "blocked forge", "no result", labels="forge",
                   setup=lambda: (self.ba_runs(), self.ok_run(101, "2026-11-18T09:00:08Z")),
                   comments=[cm(result("ba", "complete"), "2026-11-18T08:00:00Z")])

    def test_hand_off_when_the_token_runs_out(self):
        """ADR-0028: a new orchestrator run keeps waiting, with the same agent, dispatch time, stages and seconds."""
        slow = lambda: (self.ba_runs(), self.run_info(101, "in_progress", "", "2026-11-18T09:00:08Z"))
        self.check("hand-off", "forge-ba forge-orchestrator", "forge", labels="forge", setup=slow, FORGE_TOKEN_BUDGET="0")
        line = [l for l in self.read("dispatch.log").splitlines() if "forge-orchestrator.yml" in l][0]
        for field in ("-f target=o/r", "-f issue=12", "-f agent=ba", "-f since=2026-11-18T08:59:50Z", "-f stages=0"):
            self.assertIn(field, line)
        self.assertRegex(line, r"-f waited=[0-9]+")

    def test_token_budget_is_capped(self):
        import importlib.util, os
        from harness import FORGE
        spec = importlib.util.spec_from_file_location("orchestrate", os.path.join(FORGE, "orchestrate.py"))
        o = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(o)
        orch = o.Orchestrator.__new__(o.Orchestrator)
        for raw, want in (("", 3300), ("30", 30), ("99999", 3300), ("x", 3300), (" 60 ", 60)):
            orch.env = {"FORGE_TOKEN_BUDGET": raw}
            self.assertEqual(orch.token_budget(), want, raw)

    def test_resume(self):
        R = dict(FORGE_KIND="resume", P_AGENT="ba", P_SINCE="2026-11-18T08:59:50Z", P_STAGES="0", P_WAITED="0",
                 FORGE_AWAIT="1", AWAIT_INTERVAL="0", AWAIT_FIND_TRIES="2", AWAIT_MAX="1000", FORGE_NOW="2026-11-18T09:30:00Z")
        ok = lambda: (self.ba_runs(), self.ok_run(101, "2026-11-18T09:00:08Z"))
        done = [cm(result("ba", "complete"), "2026-11-18T09:05:00Z")]
        with self.subTest("resume reads the result and goes on"):
            self.orchestrate(labels="forge", setup=ok, comments=done, **R)
            self.assertIn("forge-planner-claude.yml", self.read("dispatch.log"), self.stderr)
            self.assertNotIn("forge-ba-claude.yml", self.read("dispatch.log"))
            self.assertEqual(self.unhandled(), "")
        slow = lambda: (self.ba_runs(), self.run_info(101, "in_progress", "", "2026-11-18T09:00:08Z"))
        self.check("time already waited still counts", "", "blocked forge", "did not finish", labels="forge",
                   setup=slow, **{**R, "P_WAITED": "1000"})
        self.check("stages already spent still count", "", "blocked forge", "too many stages", labels="forge",
                   setup=ok, comments=done, **{**R, "P_STAGES": "12"})
        self.check("bad inputs do nothing", "", "forge", labels="forge", setup=ok, comments=done,
                   **{**R, "P_AGENT": "infra"})

    def test_never_retried_on_another_engine(self):
        """ADR-0024: whatever the conclusion, the request is blocked with the reason and exactly one run was dispatched."""
        for c in ("failure", "timed_out", "cancelled", "action_required", "neutral", "skipped"):
            def one_run(c=c):
                self.runs_of("ba", [run_json(201, title("ba"), "2026-11-18T09:00:05Z")])
                self.run_info(201, "completed", c, "2026-11-18T09:20:00Z")
            self.check(f"{c} blocks the request", "forge-ba", "blocked forge", "ended with", labels="forge", setup=one_run)
            self.assertEqual(self.dispatches("forge-ba-claude.yml"), 1, c)

        def no_result():
            self.runs_of("ba", [run_json(201, title("ba"), "2026-11-18T09:00:05Z")])
            self.ok_run(201, "2026-11-18T09:00:08Z")
        self.check("success without result: blocked", "forge-ba", "blocked forge", "produced no result", labels="forge", setup=no_result)
        self.assertEqual(self.dispatches("forge-ba-claude.yml"), 1)
        self.check("failed run: blocked, not read as a result", "forge-ba", "blocked forge", "ended with", labels="forge",
                   setup=lambda: (self.runs_of("ba", [run_json(201, title("ba"), "2026-11-18T09:00:05Z")]),
                                  self.run_info(201, "completed", "failure", "2026-11-18T09:00:08Z")),
                   comments=[cm(result("ba", "complete"), "2026-11-18T09:05:00Z")])


if __name__ == "__main__":
    unittest.main()
