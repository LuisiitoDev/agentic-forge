#!/usr/bin/env python3
"""Tests for the intake decision logic (docs/intake-spec.md). No network, no GitHub: `gh` is the fake in tests/bin."""
import importlib.util
import json
import os
import unittest

from harness import FORGE, FakeHub

spec = importlib.util.spec_from_file_location("intake", os.path.join(FORGE, "intake.py"))
intake = importlib.util.module_from_spec(spec)
spec.loader.exec_module(intake)

DEFAULTS = dict(EVENT_NAME="issues", ACTION="labeled", REPO="me/app", SENDER="alice", SENDER_TYPE="User", ISSUE="7",
                IS_PR="false", LABEL="forge", ISSUE_LABELS="", BOT_LOGIN="my-forge[bot]", ALLOWED_TARGETS="me/app",
                DELIVERY="run-1-1")


def dec(**kw):
    return intake.decide({**DEFAULTS, **kw})


def kind(out):
    return out.split(" ", 1)[0]


def payload(out):
    return json.loads(out[len("dispatch "):])


class Decide(unittest.TestCase):
    def ignored(self, name, **kw):
        with self.subTest(name):
            self.assertEqual(kind(dec(**kw)), "ignore", dec(**kw))

    def dispatched(self, name, **kw):
        with self.subTest(name):
            self.assertEqual(kind(dec(**kw)), "dispatch", dec(**kw))

    def test_labels(self):
        self.dispatched("I-01 label forge", LABEL="forge")
        self.dispatched("I-02 label ready-for-dev", LABEL="ready-for-dev")
        for l in ("bug", "forge:claude", "not-forge", "forge ", "Forge", ""):
            self.ignored(f"I-03 label [{l}]", LABEL=l)

    def test_events(self):
        self.ignored("I-04 opened", ACTION="opened")
        self.ignored("I-04 unlabeled", ACTION="unlabeled")
        self.ignored("I-04 edited", EVENT_NAME="issue_comment", ACTION="edited", ISSUE_LABELS="forge")
        self.ignored("I-14 push", EVENT_NAME="push")
        self.ignored("I-14 empty", EVENT_NAME="")

    def test_comments_never_trigger(self):
        """ADR-0029: labels are the only trigger, so no comment is forwarded, not even on a request."""
        self.ignored("I-05 comment on a request", EVENT_NAME="issue_comment", ACTION="created", ISSUE_LABELS="forge")

    def test_clarified(self):
        r = dec(LABEL="clarified", ISSUE_LABELS="bug\nforge\nneeds-clarification")
        self.assertEqual(kind(r), "dispatch")
        self.assertEqual(payload(r)["label"], "clarified")
        self.ignored("I-06 no forge label", LABEL="clarified", ISSUE_LABELS="bug")
        self.ignored("I-06 only forge:claude", LABEL="clarified", ISSUE_LABELS="forge:claude")
        self.ignored("I-06 substring", LABEL="clarified", ISSUE_LABELS="not-forge")
        self.ignored("I-07 pull request", LABEL="clarified", ISSUE_LABELS="forge", IS_PR="true")
        self.ignored("I-07 pr on label event", IS_PR="true")

    def test_bots(self):
        self.ignored("I-08 type Bot", SENDER_TYPE="Bot")
        self.ignored("I-08 [bot] suffix", SENDER="someone[bot]")
        self.ignored("I-08 BOT_LOGIN case", SENDER="MY-FORGE[bot]", SENDER_TYPE="User")
        self.ignored("I-08 BOT_LOGIN plain", SENDER="forgebot", BOT_LOGIN="forgebot")

    def test_allowlist(self):
        self.ignored("I-09 off allowlist", REPO="evil/repo")
        self.ignored("I-09 prefix only", REPO="me/app-extra")
        self.ignored("I-09 suffix only", REPO="me/ap")
        self.ignored("I-09 empty list", ALLOWED_TARGETS="")
        self.ignored("I-09 blank list", ALLOWED_TARGETS=" \n , ")
        self.dispatched("I-10 messy list", REPO="ME/APP", ALLOWED_TARGETS=" other/x ,\n me/app\n,")
        for t in ("app", "me/app/extra", "me/a pp", "/app", "me/", "me/app;x", "me/app\n"):
            self.ignored(f"I-11 repo [{t!r}]", REPO=t, ALLOWED_TARGETS=t)

    def test_malformed_values(self):
        for n in ("abc", "0", "007", "-1", "", "1 2", "7.5", "7\n"):
            self.ignored(f"I-12 issue [{n!r}]", ISSUE=n)
        for s in ("a b", 'a"b', "x$(touch /tmp/x)", "x`touch /tmp/x`", "a\nb", "a;b", "", "alice\n"):
            out = dec(SENDER=s)
            with self.subTest(f"I-13 sender [{s!r}]"):
                self.assertEqual(kind(out), "ignore")
                for leak in ("$(", "`", "touch", "a b"):
                    self.assertNotIn(leak, out, "I-17 the reason echoes the input")

    def test_payload(self):
        r = dec(LABEL="forge", DELIVERY="run-9-2")
        self.assertEqual(payload(r), {"action": "labeled", "delivery": "run-9-2", "event": "issues", "issue": 7,
                                      "label": "forge", "sender": "alice", "target": "me/app"})
        self.assertIsInstance(payload(r)["issue"], int)
        r = dec(LABEL="clarified", ISSUE_LABELS="forge", SENDER="bob", REPO="Me/App")
        self.assertEqual(payload(r), {"action": "labeled", "delivery": "run-1-1", "event": "issues", "issue": 7,
                                      "label": "clarified", "sender": "bob", "target": "Me/App"})

    def test_ignore_is_one_line_and_deterministic(self):
        for kw in (dict(ACTION="opened"), dict(SENDER_TYPE="Bot"), dict(ALLOWED_TARGETS=""), dict(ISSUE="x"), dict(SENDER="a\nb")):
            out = dec(**kw)
            with self.subTest(kw):
                self.assertTrue(out.startswith("ignore "))
                self.assertNotIn("\n", out)
        self.assertEqual(dec(), dec())


class Send(FakeHub):
    PAYLOAD = '{"event":"issues","action":"labeled","target":"me/app","issue":7,"sender":"alice","delivery":"run-1-1","label":"forge"}'

    def send(self, **kw):
        self.reset()
        env = self.env(**{"ENGINE_REPO": "eng/forge", "DISPATCH_TOKEN": "dispatch-tok", "GH_TOKEN": "ambient-tok", **kw})
        try:
            intake.send(self.PAYLOAD, env)
            return True
        except RuntimeError:
            return False

    def test_success(self):
        self.assertTrue(self.send())
        event = json.loads(self.read("dispatch_event.json"))
        # ADR-0031: workflow_dispatch of the orchestrator, which needs only Actions: write
        self.assertEqual(event["path"], "repos/eng/forge/actions/workflows/forge-orchestrator.yml/dispatches")
        self.assertEqual(event["body"], {"ref": "main", "inputs": {"target": "me/app", "issue": "7", "label": "forge",
                                                                   "sender": "alice", "delivery": "run-1-1"}})
        self.assertEqual(set(self.read("tokens.log").split()), {"dispatch-tok"})

    def test_failure_is_an_error(self):
        self.assertFalse(self.send(DISPATCH_FAIL="1"))

    def test_no_token_never_falls_back_to_the_ambient_one(self):
        self.assertFalse(self.send(DISPATCH_TOKEN=""))
        self.assertEqual(self.read("tokens.log"), "")


if __name__ == "__main__":
    unittest.main()
