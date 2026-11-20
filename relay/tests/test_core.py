import hashlib
import hmac
import json
import os
import sys
import unittest
import urllib.error

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from forge_relay import core  # noqa: E402

SECRET = "s3cret"


def cfg(**kw):
    args = dict(webhook_secret=SECRET, engine_repo="me/engine", dispatch_token="tok",
                allowed_targets="Me/App, me/other", bot_login="my-forge[bot]")
    args.update(kw)
    return core.Config(**args)


def labeled(label="forge", sender="alice", target="me/app", number=7, stype="User"):
    return {"action": "labeled", "label": {"name": label}, "issue": {"number": number, "labels": []},
            "repository": {"full_name": target}, "sender": {"login": sender, "type": stype}}


def commented(labels=("forge",), sender="alice", target="me/app", pr=False):
    issue = {"number": 7, "labels": [{"name": l} for l in labels]}
    if pr:
        issue["pull_request"] = {}
    return {"action": "created", "issue": issue, "repository": {"full_name": target},
            "sender": {"login": sender, "type": "User"}}


def post(body, event="issues", secret=SECRET, c=None, sig=None, send=None):
    raw = json.dumps(body).encode() if not isinstance(body, bytes) else body
    if sig is None:
        sig = "sha256=" + hmac.new(secret.encode(), raw, hashlib.sha256).hexdigest()
    sent = []
    headers = {"X-Hub-Signature-256": sig, "X-GitHub-Event": event, "X-GitHub-Delivery": "d-1"}
    result = core.handle(headers, raw, c or cfg(), send or (lambda cf, p: sent.append(p)))
    return result, sent


class RelayTests(unittest.TestCase):
    def test_label_forge_dispatches_minimal_payload(self):
        (status, _), sent = post(labeled())
        self.assertEqual(status, 202)
        self.assertEqual(sent, [{"event": "issues", "action": "labeled", "target": "me/app", "issue": 7,
                                 "sender": "alice", "delivery": "d-1", "label": "forge"}])

    def test_ready_for_dev_dispatches(self):
        (status, _), sent = post(labeled("ready-for-dev"))
        self.assertEqual((status, sent[0]["label"]), (202, "ready-for-dev"))

    def test_bad_or_missing_signature_is_401_and_sends_nothing(self):
        for sig in ("sha256=" + "0" * 64, "", "nope"):
            (status, _), sent = post(labeled(), sig=sig)
            self.assertEqual((status, sent), (401, []))
        (status, _), sent = post(labeled(), secret="wrong")
        self.assertEqual((status, sent), (401, []))

    def test_ping(self):
        (status, msg), sent = post({"zen": "x"}, event="ping")
        self.assertEqual((status, msg, sent), (200, "pong", []))

    def test_target_not_on_allowlist_is_ignored(self):
        (status, msg), sent = post(labeled(target="evil/repo"))
        self.assertEqual((status, sent), (200, []))
        self.assertIn("allowlist", msg)

    def test_allowlist_is_case_insensitive(self):
        (status, _), sent = post(labeled(target="ME/APP"))
        self.assertEqual((status, len(sent)), (202, 1))

    def test_bot_senders_are_ignored(self):
        for sender, t in (("my-forge[bot]", "Bot"), ("someone[bot]", "User"), ("MY-FORGE[bot]", "User"),
                          ("octo", "Bot")):
            (status, msg), sent = post(labeled(sender=sender, stype=t))
            self.assertEqual((status, sent), (200, []), sender)
            self.assertIn("bot", msg)

    def test_unhandled_label_and_action(self):
        self.assertEqual(post(labeled("bug"))[1], [])
        body = labeled(); body["action"] = "unlabeled"
        self.assertEqual(post(body)[1], [])

    def test_unhandled_event(self):
        self.assertEqual(post(labeled(), event="push")[1], [])

    def test_comment_only_on_forge_issues(self):
        (status, _), sent = post(commented(), event="issue_comment")
        self.assertEqual((status, sent[0]["event"], sent[0]["action"]), (202, "issue_comment", "created"))
        self.assertNotIn("label", sent[0])
        self.assertEqual(post(commented(labels=("bug",)), event="issue_comment")[1], [])

    def test_comment_on_pull_request_is_ignored(self):
        self.assertEqual(post(commented(pr=True), event="issue_comment")[1], [])

    def test_malformed_fields_are_ignored(self):
        for bad in (labeled(number="7"), labeled(number=0), labeled(number=True), labeled(sender="a b"),
                    labeled(target="no-slash"), labeled(target="a/b/c")):
            self.assertEqual(post(bad, c=cfg(allowed_targets="a/b/c,no-slash,me/app"))[1], [], bad)

    def test_invalid_json_and_non_object(self):
        self.assertEqual(post(b"{not json")[0][0], 400)
        self.assertEqual(post(b"[1]")[0][0], 400)

    def test_oversized_body(self):
        self.assertEqual(core.handle({}, b"x" * (core.MAX_BODY + 1), cfg())[0], 413)

    def test_dispatch_failure_is_502(self):
        def boom(c, p):
            raise urllib.error.URLError("down")
        self.assertEqual(post(labeled(), send=boom)[0][0], 502)

    def test_dispatch_request_shape(self):
        seen = {}

        class Resp:
            status = 204
            def __enter__(self): return self
            def __exit__(self, *a): return False

        def opener(req, timeout):
            seen["url"], seen["body"], seen["auth"] = req.full_url, json.loads(req.data), req.get_header("Authorization")
            return Resp()

        core.dispatch(cfg(), {"issue": 7}, opener=opener)
        self.assertEqual(seen["url"], "https://api.github.com/repos/me/engine/dispatches")
        self.assertEqual(seen["body"], {"event_type": "forge", "client_payload": {"issue": 7}})
        self.assertEqual(seen["auth"], "Bearer tok")

    def test_config_validation(self):
        with self.assertRaises(ValueError):
            cfg(dispatch_token="").validate()
        with self.assertRaises(ValueError):
            cfg(engine_repo="bad").validate()
        cfg().validate()


if __name__ == "__main__":
    unittest.main()
