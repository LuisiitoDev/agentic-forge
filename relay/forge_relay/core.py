"""Stateless relay core (ADR-0007). No Azure imports here, so it is testable with the standard library.

For each GitHub App webhook it: verifies the signature, ignores what the engine does not handle (including
events caused by bots), checks the target against the allowlist, and sends a minimal repository_dispatch to
the engine repository. It keeps no state.
"""
import hashlib
import hmac
import json
import re
import urllib.error
import urllib.request

TARGET_RE = re.compile(r"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$")
SENDER_RE = re.compile(r"^[A-Za-z0-9-]+(\[bot\])?$")
REQUEST_LABEL = "forge"
HANDLED_LABELS = ("forge", "ready-for-dev")  # the labels the orchestrator acts on
MAX_BODY = 1024 * 1024


class Config:
    def __init__(self, webhook_secret, engine_repo, dispatch_token, allowed_targets, bot_login=""):
        self.webhook_secret = webhook_secret
        self.engine_repo = engine_repo  # owner/name of the engine repository
        self.dispatch_token = dispatch_token
        self.allowed_targets = {t.strip().lower() for t in re.split(r"[,\n]", allowed_targets) if t.strip()}
        self.bot_login = bot_login.strip().lower()

    def validate(self):
        if not self.webhook_secret or not self.dispatch_token:
            raise ValueError("webhook secret and dispatch token are required")
        if not TARGET_RE.match(self.engine_repo or ""):
            raise ValueError("engine repository must be owner/name")


def verify_signature(secret, body, header):
    """Constant-time check of X-Hub-Signature-256. Anything missing or malformed is invalid."""
    if not secret or not header or not header.startswith("sha256="):
        return False
    expected = "sha256=" + hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, header)


def _is_bot(sender, cfg):
    login = (sender.get("login") or "").lower()
    return sender.get("type") == "Bot" or login.endswith("[bot]") or (cfg.bot_login and login == cfg.bot_login)


def build_payload(event, body, delivery, cfg):
    """Return (payload, None) to dispatch, or (None, reason) to ignore. Never raises on a valid JSON object."""
    action = body.get("action")
    issue = body.get("issue") or {}
    repo = body.get("repository") or {}
    sender = body.get("sender") or {}
    target = repo.get("full_name") or ""
    login = sender.get("login") or ""

    if event not in ("issues", "issue_comment"):
        return None, "event not handled"
    if (event, action) not in (("issues", "labeled"), ("issue_comment", "created")):
        return None, "action not handled"
    if _is_bot(sender, cfg):
        return None, "sender is a bot"  # loop prevention: the engine's own labels and comments
    if not TARGET_RE.match(target):
        return None, "malformed target"
    if target.lower() not in cfg.allowed_targets:
        return None, "target is not on the allowlist"
    if not SENDER_RE.match(login):
        return None, "malformed sender"
    number = issue.get("number")
    if not isinstance(number, int) or isinstance(number, bool) or number <= 0:
        return None, "malformed issue number"
    if "pull_request" in issue:
        return None, "pull requests are not requests"

    payload = {"event": event, "action": action, "target": target, "issue": number,
               "sender": login, "delivery": delivery or ""}
    if event == "issues":
        label = (body.get("label") or {}).get("name")
        if label not in HANDLED_LABELS:
            return None, "label not handled"
        payload["label"] = label
    else:
        names = {l.get("name") for l in issue.get("labels") or [] if isinstance(l, dict)}
        if REQUEST_LABEL not in names:
            return None, "comment is not on a forge request"
    return payload, None


def dispatch(cfg, payload, opener=urllib.request.urlopen):
    """repository_dispatch to the engine repository. Raises on any failure so GitHub can redeliver."""
    req = urllib.request.Request(
        "https://api.github.com/repos/%s/dispatches" % cfg.engine_repo,
        data=json.dumps({"event_type": "forge", "client_payload": payload}).encode(),
        method="POST",
        headers={"Authorization": "Bearer " + cfg.dispatch_token, "Accept": "application/vnd.github+json",
                 "X-GitHub-Api-Version": "2022-11-28", "Content-Type": "application/json",
                 "User-Agent": "forge-relay"},
    )
    with opener(req, timeout=10) as resp:
        if resp.status != 204:
            raise RuntimeError("dispatch returned %s" % resp.status)


def handle(headers, raw_body, cfg, send=dispatch):
    """Process one webhook. Returns (http_status, message)."""
    h = {k.lower(): v for k, v in headers.items()}
    if len(raw_body) > MAX_BODY:
        return 413, "payload too large"
    if not verify_signature(cfg.webhook_secret, raw_body, h.get("x-hub-signature-256")):
        return 401, "invalid signature"
    event = h.get("x-github-event", "")
    if event == "ping":
        return 200, "pong"
    try:
        body = json.loads(raw_body)
    except ValueError:
        return 400, "invalid json"
    if not isinstance(body, dict):
        return 400, "invalid json"
    payload, reason = build_payload(event, body, h.get("x-github-delivery", ""), cfg)
    if payload is None:
        return 200, "ignored: " + reason
    try:
        send(cfg, payload)
    except (urllib.error.URLError, OSError, RuntimeError):
        return 502, "dispatch failed"  # non-2xx: the delivery can be redelivered; stages are idempotent
    return 202, "dispatched"
