#!/usr/bin/env python3
"""Agentic Forge orchestrator. Deterministic routing: no model is involved here.

Two kinds of events are handled (see .github/workflows/forge-orchestrator.yml):
  human  - a repository_dispatch from the intake workflow: a human labeled or commented on a request issue
  agent  - a forge-* agent workflow finished: read its result and decide the next stage
The workflow only receives human events. With FORGE_AWAIT=1 the script then waits for each agent it dispatches
and handles the result itself, as an `agent` event, until a human gate or a failure ends the chain (ADR-0018).
GitHub does not deliver `workflow_run` for runs started with the workflow token, so there is no agent event.

Authority rules (ADR-0008): only a human with write access may start or approve. Events caused by bots never
do. Agents never name labels; this script maps their `status` to labels and to the next stage.

Required environment (set by the workflow):
  FORGE_KIND            human | agent | resume
  TARGET, ISSUE         the request: owner/name and issue number (already validated)
  TARGET_TOKEN          GitHub App installation token scoped to TARGET only
  ENGINE_TOKEN          the workflow's own token, used only to dispatch agent workflows
  ENGINE_REPO, ENGINE_REF   this repository and the ref this run was started on (the default branch for a human
                            event; a resume keeps the ref of the run that handed off)
  FORGE_BOT_LOGIN       login of the App's bot; only its comments are trusted results
  RUN_URL               link to this orchestrator run, for comments
Optional policy (engine configuration, never read from the target):
  FORGE_ALLOW_SELF_APPROVAL   "true" lets the person who opened a request approve its plan. Default: false
  FORGE_SENSITIVE_PATHS       path prefixes, comma or newline separated. A plan that touches one needs an
                              approver with the admin or maintain role. Default: none
human:  P_EVENT, P_ACTION, P_SENDER, P_LABEL
agent:  AGENT (ba|planner|coder|reviewer), CONCLUSION, RUN_STARTED_AT (set by the wait; the tests set them)
resume: P_AGENT, P_SINCE, P_STAGES, P_WAITED (a wait handed off by an earlier run when its token ran out, ADR-0028)
Waiting for agent runs (ADR-0018), all optional:
  FORGE_AWAIT          "1" turns the wait on. Default: off
  AWAIT_INTERVAL       seconds between polls. Default: 20
  AWAIT_FIND_TRIES     polls to find the dispatched run before giving up. Default: 9
  AWAIT_MAX            seconds to wait for one agent run to complete. Default: 5400
  FORGE_MAX_STAGES     agent runs awaited in one orchestration. Default: 12
  FORGE_TOKEN_BUDGET   seconds since the script started after which it hands the wait off to a new orchestrator
                       run (ADR-0028). The target token is a GitHub App installation token and expires after one
                       hour, so it must still be valid then. Default: 3300
  FORGE_NOW            the dispatch time (ISO 8601 UTC), for the tests. Default: the clock

Python, standard library only. `gh` and `yq` are external tools.
"""
import base64
import datetime
import os
import re
import subprocess
import sys
import time
import urllib.parse
import json

MARK = "<!-- forge:orchestrator -->"
STATE_LABELS = ("needs-clarification", "planning", "spec-review", "needs-review", "blocked")
AGENT_TITLES = {"ba": "BA", "planner": "Planner", "coder": "Coder", "reviewer": "Reviewer"}


def log(msg: str) -> None:
    print(f"[forge] {msg}", file=sys.stderr, flush=True)


# ---------------------------------------------------------------- reading the agents' result comments
def yaml_block(body: str) -> list:
    """The lines of the first ```yaml fence of a comment body."""
    out, inside = [], False
    for line in body.split("\n"):
        if line.startswith("```yaml"):
            if inside:
                continue
            inside = True
            continue
        if line.startswith("```"):
            if inside:
                break
            continue
        if inside:
            out.append(line)
    return out


def yaml_field(lines: list, key: str) -> str:
    """Flat `key: value  # comment` scalar. Empty if absent."""
    for line in lines:
        if line.startswith(key + ":"):
            v = re.sub(r"^" + re.escape(key) + r":\s*", "", line)
            v = re.sub(r"\s+#.*$", "", v)
            return re.sub(r'^"|"$', "", v)
    return ""


def yaml_nested(lines: list, parent: str, key: str) -> str:
    """`parent:` then an indented `key: value` scalar."""
    in_parent = False
    for line in lines:
        if re.match(r"^" + re.escape(parent) + r":\s*$", line):
            in_parent = True
            continue
        if in_parent and re.match(r"^[^\s]", line):
            in_parent = False
        if in_parent and re.match(r"^\s+" + re.escape(key) + ":", line):
            v = re.sub(r"^\s+" + re.escape(key) + r":\s*", "", line)
            return re.sub(r"\s+#.*$", "", v)
    return ""


def _scalar(v: str) -> str:
    v = re.sub(r"\s+#.*$", "", v).strip()
    if len(v) >= 2 and v[0] == v[-1] and v[0] in "\"'":
        v = v[1:-1]
    return v


def yaml_list(lines: list, key: str):
    """`key:` then a block list (`  - a`), or a flow list (`key: [a, b]`). [] when the key is absent. None when the
    key is there but its value is neither: the caller must treat that as unreadable, never as an empty list."""
    out, inside = [], False
    for line in lines:
        m = re.match(r"^" + re.escape(key) + r":(.*)$", line)
        if m and not inside:
            rest = re.sub(r"\s+#.*$", "", m.group(1)).strip()
            if not rest:
                inside = True
                continue
            if rest.startswith("[") and rest.endswith("]"):
                body = rest[1:-1].strip()
                return [_scalar(x) for x in body.split(",")] if body else []
            return None
        if inside and re.match(r"^\s*-\s", line):
            out.append(_scalar(re.sub(r"^\s*-\s*", "", line)))
            continue
        if inside and re.match(r"^\s*$", line):
            continue
        if inside and re.match(r"^[^\s]", line):
            break
        if inside:
            return None
    return out


def result_status(res: dict) -> str:
    return yaml_field(yaml_block(res["body"]), "status")


def result_created(res: dict) -> str:
    return res["created_at"]


def result_tasks(res: dict, key: str) -> str:
    return yaml_nested(yaml_block(res["body"]), "tasks", key)


def as_count(value: str) -> int:
    return int(value) if re.fullmatch(r"[0-9]+", value or "") else 0


# ---------------------------------------------------------------- path rules
def lc(s: str) -> str:
    return s.lower()


def norm_path(p: str) -> str:
    """Lowercase, no leading ./ or /, no trailing /. Case-insensitive on purpose: stricter, and safe on any filesystem."""
    p = lc(p)
    if p.startswith("./"):
        p = p[2:]
    if p.startswith("/"):
        p = p[1:]
    if p.endswith("/"):
        p = p[:-1]
    return p


def sensitive_hits(paths, sensitive: str) -> list:
    """The paths under a sensitive prefix. A path with `..` is a hit, because it could point outside the prefix it
    appears to be under. Matches whole path segments: `infra/` does not match `infra-docs/x`."""
    prefixes = []
    for s in sensitive.replace(",", "\n").split("\n"):
        n = norm_path(re.sub(r"\s+", "", s))
        if n:
            prefixes.append(n)
    hits = []
    for p in paths:
        if not p:
            continue
        n = norm_path(p)
        if "/../" in f"/{n}/":
            hits.append(p)
            continue
        if any(n == s or n.startswith(s + "/") for s in prefixes):
            hits.append(p)
    return hits


def scope_violations_of(files, issue: str, allowed) -> list:
    """Files outside the plan directory and outside the paths the target allows (`paths.app`). Same rule as
    validate_changes.py: an entry allows itself and what is under it. Case-sensitive on purpose (git paths are), which
    is the strict side for an allowlist; sensitive_hits is case-insensitive, the strict side for a denylist. A path
    with `..` is always a violation."""
    prefixes = [p for p in (norm_scope(a) for a in allowed if a) if p]
    bad = []
    for f in files:
        n = norm_scope(f)
        if "/../" in f"/{n}/":
            bad.append(f)
            continue
        if n.startswith(f".forge/requests/{issue}/"):
            continue
        if any(n == p or n.startswith(p + "/") for p in prefixes):
            continue
        bad.append(f)
    return bad


def norm_scope(p: str) -> str:
    p = p.strip()
    if p.startswith("./"):
        p = p[2:]
    return p.lstrip("/").rstrip("/")


def unapproved_sensitive(changed, declared, sensitive: str) -> list:
    """Changed files under a sensitive prefix that the approved plan did not declare (the file itself or a directory
    above it). Those were never seen by an admin or maintain approver, so the implementation is not reviewed."""
    covered = [norm_path(d) for d in sensitive_hits(declared, sensitive)]
    out = []
    for f in sensitive_hits(changed, sensitive):
        n = norm_path(f)
        if "/../" in f"/{n}/" or not any(n == c or n.startswith(c + "/") for c in covered):
            out.append(f)
    return out


# ---------------------------------------------------------------- the orchestrator
class Orchestrator:
    def __init__(self, env=None):
        e = os.environ if env is None else env
        self.env = e
        self.started = time.monotonic()
        self.kind = e.get("FORGE_KIND", "")
        self.target = e["TARGET"]
        self.issue = e["ISSUE"]
        self.target_token = e["TARGET_TOKEN"]
        self.engine_token = e["ENGINE_TOKEN"]
        self.engine_repo = e["ENGINE_REPO"]
        self.engine_ref = e["ENGINE_REF"]
        self.bot = e["FORGE_BOT_LOGIN"]
        self.run_url = e["RUN_URL"]
        self.allow_self_approval = e.get("FORGE_ALLOW_SELF_APPROVAL", "false") == "true"
        self.sensitive = e.get("FORGE_SENSITIVE_PATHS", "")
        self.p_event, self.p_action = e.get("P_EVENT", ""), e.get("P_ACTION", "")
        self.p_sender, self.p_label = e.get("P_SENDER", ""), e.get("P_LABEL", "")
        self.agent = e.get("AGENT", "")
        self.conclusion = e.get("CONCLUSION", "")
        self.run_started_at = e.get("RUN_STARTED_AT", "")
        self.pending_agent = ""
        self.pending_since = ""
        # A resumed orchestration (ADR-0028) carries on with the wait the previous run handed off.
        self.stages_done = int(e.get("P_STAGES") or 0) if re.fullmatch(r"[0-9]*", e.get("P_STAGES", "")) else 0
        self.waited = int(e.get("P_WAITED") or 0) if re.fullmatch(r"[0-9]*", e.get("P_WAITED", "")) else 0

    # ---- GitHub access
    def gh(self, token: str, *args: str, check: bool = True) -> str:
        env = dict(self.env, GH_TOKEN=token)
        r = subprocess.run(["gh", *args], env=env, capture_output=True, text=True)
        if r.returncode != 0 and check:
            raise RuntimeError(f"gh {' '.join(args[:2])} failed ({r.returncode}): {r.stderr.strip()}")
        return r.stdout if r.returncode == 0 else ""

    def tgt(self, *args: str, check: bool = True) -> str:
        return self.gh(self.target_token, *args, check=check)

    def eng(self, *args: str, check: bool = True) -> str:
        return self.gh(self.engine_token, *args, check=check)

    def issue_labels(self) -> list:
        out = self.tgt("api", f"repos/{self.target}/issues/{self.issue}/labels", "--paginate", "--jq", ".[].name")
        return out.split("\n")

    def has_label(self, name: str) -> bool:
        return name in self.issue_labels()

    def add_label(self, name: str) -> None:
        self.tgt("api", "-X", "POST", f"repos/{self.target}/issues/{self.issue}/labels", "-f", f"labels[]={name}")

    def remove_label(self, name: str) -> None:
        enc = urllib.parse.quote(name, safe="")
        self.tgt("api", "-X", "DELETE", f"repos/{self.target}/issues/{self.issue}/labels/{enc}", check=False)

    def comment(self, text: str) -> None:
        self.tgt("api", "-X", "POST", f"repos/{self.target}/issues/{self.issue}/comments", "-f", f"body={MARK}\n{text}")

    def ensure_labels(self) -> None:
        for name in ("forge", "ready-for-dev", "clarified", *STATE_LABELS):
            self.tgt("api", "-X", "POST", f"repos/{self.target}/labels", "-f", f"name={name}", "-f", "color=5319e7",
                     check=False)

    def set_state(self, state: str) -> None:
        """Exactly one state label at a time."""
        for name in STATE_LABELS:
            if name != state:
                self.remove_label(name)
        self.add_label(state)

    def clear_state(self) -> None:
        for name in STATE_LABELS:
            self.remove_label(name)

    # ---- results written by agents
    def comments_of(self, number: str) -> list:
        out = self.tgt("api", f"repos/{self.target}/issues/{number}/comments", "--paginate", "--jq", ".[]")
        dec, i, items = json.JSONDecoder(), 0, []
        while True:
            while i < len(out) and out[i].isspace():
                i += 1
            if i >= len(out):
                return items
            obj, i = dec.raw_decode(out, i)
            items.append(obj)

    def latest_result(self, agent: str):
        """The latest trusted result comment of an agent, or None. Trusted = authored by the engine's bot and
        starting with the agent's result heading (`## BA result: complete`). Results live on the request issue
        (BA, first plan) and on the plan pull request (everything after), so both are read."""
        pr = self.plan_pr()
        comments = self.comments_of(self.issue)
        if pr:
            comments += self.comments_of(pr)
        heading = f"## {AGENT_TITLES.get(agent, agent)} result:"
        mine = [c for c in comments
                if (c.get("user") or {}).get("login") == self.bot and (c.get("body") or "").startswith(heading)]
        mine.sort(key=lambda c: c["created_at"])
        return mine[-1] if mine else None

    def plan_pr(self) -> str:
        owner = self.target.split("/")[0]
        return self.tgt("api", f"repos/{self.target}/pulls?head={owner}:forge/issue-{self.issue}&state=open",
                        "--jq", ".[0].number // empty").strip()

    # ---- dispatching agents
    def dispatch(self, wf: str, thread: str, *extra: str) -> None:
        """Runs the agent's workflow, `forge-<agent>-claude.yml` (Claude Code inside AWF, ADR-0022). `thread` is where
        the agent posts its result: the request issue before the plan pull request exists, the plan pull request after."""
        log(f"dispatch {wf} for {self.target}#{self.issue}, result on #{thread}")
        self.eng("workflow", "run", f"{wf}-claude.yml", "--repo", self.engine_repo, "--ref", self.engine_ref,
                 "-f", f"target={self.target}", "-f", f"issue={self.issue}", "-f", f"thread={thread}",
                 "-f", f"target_repo_name={self.target.split('/')[-1]}", *extra)
        # Remember it: with FORGE_AWAIT the orchestrator waits for this run (see `drive`).
        self.pending_agent = wf[len("forge-"):] if wf.startswith("forge-") else wf
        self.pending_since = self.env.get("FORGE_NOW") or datetime.datetime.now(datetime.timezone.utc).strftime(
            "%Y-%m-%dT%H:%M:%SZ")

    def fail_stage(self, reason: str) -> None:
        log(f"blocked: {reason}")
        self.set_state("blocked")
        self.comment(f"**Blocked.** {reason}\n\nSee the orchestrator run: {self.run_url}")

    def refuse(self, reason: str) -> None:
        log(f"refused: {reason}")
        self.remove_label("ready-for-dev")
        self.comment(f"**Not approved.** {reason}")

    # ---- authority (ADR-0011)
    def require_human_writer(self, user: str) -> bool:
        """A human (not a bot) with write access to the target. Checked here, never taken from the payload's claims."""
        if not user:
            log("no sender")
            return False
        try:
            kind = self.tgt("api", f"users/{user}", "--jq", ".type").strip()
        except RuntimeError:
            log(f"cannot resolve {user}")
            return False
        if kind != "User":
            log(f"{user} is a {kind}, not a human")
            return False
        try:
            perm = self.tgt("api", f"repos/{self.target}/collaborators/{user}/permission", "--jq", ".permission").strip()
        except RuntimeError:
            log(f"no permission for {user}")
            return False
        if perm in ("admin", "write"):
            return True
        log(f"{user} has {perm} access, write is required")
        return False

    # ---- approval policy (ADR-0012)
    def requester(self) -> str:
        return self.tgt("api", f"repos/{self.target}/issues/{self.issue}", "--jq", ".user.login", check=False).strip()

    def approver_role(self, user: str) -> str:
        return self.tgt("api", f"repos/{self.target}/collaborators/{user}/permission", "--jq", ".role_name",
                        check=False).strip()

    def pr_files(self, pr: str) -> list:
        out = self.tgt("api", f"repos/{self.target}/pulls/{pr}/files", "--paginate", "--jq", ".[].filename")
        return [f for f in out.split("\n") if f]

    def plan_paths(self, plan: dict, pr: str) -> list:
        """Every path the plan declares plus every file changed on the plan branch. The wider of the two, so a plan
        cannot hide a path by leaving it out of its own list."""
        declared = self.declared_paths(plan)
        return sorted(set(declared + self.pr_files(pr)))

    @staticmethod
    def declared_paths(plan: dict) -> list:
        """The `paths` the plan lists. RuntimeError when the list is there but cannot be read: fail closed."""
        declared = yaml_list(yaml_block(plan["body"]), "paths")
        if declared is None:
            raise RuntimeError("the plan's paths list cannot be read")
        return declared

    # ---- scope gate before review
    def allowed_paths(self) -> list:
        """`paths.app` of the target's `.forge/config.yml`. Without a config, nothing is allowed."""
        raw = self.tgt("api", f"repos/{self.target}/contents/.forge/config.yml", "--jq", ".content", check=False)
        try:
            cfg = base64.b64decode(raw.strip()).decode("utf-8") if raw.strip() else ""
        except (ValueError, UnicodeDecodeError):
            cfg = ""
        if not cfg:
            return []
        r = subprocess.run(["yq", "-r", "(.paths.app // [])[]"], input=cfg, capture_output=True, text=True)
        if r.returncode != 0:
            return []
        return [re.sub(r'^"|"$', "", line) for line in r.stdout.split("\n") if line]

    def scope_violations(self, pr: str) -> list:
        return scope_violations_of(self.pr_files(pr), self.issue, self.allowed_paths())

    def start_review(self, pr: str) -> None:
        try:
            bad = self.scope_violations(pr)
        except RuntimeError:
            self.fail_stage("Could not list the files the implementation changed, so it is not reviewed.")
            return
        if bad:
            listing = "\n".join(f"- `{f}`" for f in bad)
            self.fail_stage("The implementation changed files outside the paths this target allows "
                            f"(`paths.app` in `.forge/config.yml`):\n\n{listing}")
            return
        # Sensitive paths were checked against the plan at approval. Check them again against what the Coder really
        # changed: a sensitive file the approved plan did not declare was never approved by admin or maintain.
        if self.sensitive:
            try:
                plan = self.latest_result("planner")
                if not plan:
                    raise RuntimeError("no planner result")
                hidden = unapproved_sensitive(self.pr_files(pr), self.declared_paths(plan), self.sensitive)
            except (RuntimeError, ValueError):
                self.fail_stage("Could not compare the implementation with the approved plan's sensitive paths, "
                                "so it is not reviewed.")
                return
            if hidden:
                listing = "\n".join(f"- `{f}`" for f in hidden[:10])
                self.fail_stage("The implementation changed sensitive paths that the approved plan did not declare, so "
                                "no admin or maintain approver saw them. Replan with these paths and approve again:"
                                f"\n\n{listing}")
                return
        self.dispatch("forge-reviewer", pr, "-f", f"pr={pr}", "-f", f"ref=forge/issue-{self.issue}")

    # ---- human events
    def start_request(self) -> None:
        if not self.has_label("forge"):
            log("forge label is not on the issue any more")
            return
        if any(self.has_label(name) for name in ("needs-clarification", "planning", "spec-review", "needs-review")):
            log("request already in progress")
            return
        self.ensure_labels()
        self.clear_state()
        self.dispatch("forge-ba", self.issue)

    def approve(self) -> None:
        if not self.has_label("spec-review"):
            self.refuse("The plan is not awaiting approval.")
            return

        # Two people per change: whoever opened the request may not approve its plan, unless the engine allows it.
        if not self.allow_self_approval:
            req = self.requester()
            if not req:
                self.refuse("Could not tell who opened this request, so the approval is denied.")
                return
            if lc(req) == lc(self.p_sender):
                self.refuse("You opened this request, so another person with write access must approve the plan.")
                return

        pr = self.plan_pr()
        if not pr:
            self.refuse(f"No open plan pull request was found for branch `forge/issue-{self.issue}`.")
            return
        plan = self.latest_result("planner")
        if not plan:
            self.refuse("No planner result was found.")
            return
        if result_status(plan) != "ready-for-review":
            self.refuse("The latest plan is not ready for review.")
            return

        # Sensitive paths need a stronger approver. Fails closed: if the paths or the role cannot be read, deny.
        if self.sensitive:
            try:
                paths = self.plan_paths(plan, pr)
            except RuntimeError:
                self.refuse("Could not list the files this plan changes, so the approval is denied.")
                return
            hits = sensitive_hits(paths, self.sensitive)
            if hits:
                role = self.approver_role(self.p_sender)
                if role not in ("admin", "maintain"):
                    listing = "\n".join(f"- `{h}`" for h in hits[:10])
                    self.refuse("This plan touches sensitive paths, so it must be approved by someone with the admin "
                                f"or maintain role (you have: `{role or 'unknown'}`):\n\n{listing}")
                    return

        app = as_count(result_tasks(plan, "app"))
        # The engine has no infrastructure agent (ADR-0023): a plan that still lists infrastructure tasks is not run.
        infra = as_count(result_tasks(plan, "infra"))
        if infra > 0:
            self.refuse(f"This plan has infrastructure tasks (`tasks.infra: {infra}`), and the engine does not run "
                        "infrastructure work: a person does it. Ask for a replan without them.")
            return
        self.remove_label("spec-review")
        if app > 0:
            self.dispatch("forge-coder", pr, "-f", f"pr={pr}", "-f", f"ref=forge/issue-{self.issue}")
        else:
            self.fail_stage("The plan has no application tasks.")

    def resume_after_clarification(self) -> None:
        if not self.has_label("needs-clarification"):
            log("not waiting for clarification")
            return
        ba, planner = self.latest_result("ba"), self.latest_result("planner")
        agent = "ba"
        if planner and (not ba or result_created(planner) > result_created(ba)):
            agent = "planner"
        self.remove_label("needs-clarification")
        if agent == "planner":
            self.set_state("planning")
            pr = self.plan_pr()
            if pr:
                self.dispatch("forge-planner", pr, "-f", f"pr={pr}", "-f", f"ref=forge/issue-{self.issue}")
            else:
                self.dispatch("forge-planner", self.issue)
        else:
            self.dispatch("forge-ba", self.issue)

    def handle_human(self) -> None:
        if not self.require_human_writer(self.p_sender):
            log("ignored: sender is not an authorized human")
            return
        event = f"{self.p_event}/{self.p_action}"
        if event == "issues/labeled":
            if self.p_label == "forge":
                self.start_request()
            elif self.p_label == "ready-for-dev":
                self.approve()
            elif self.p_label == "clarified":
                # A signal, not a state: consumed at once, so the next answer can add it again.
                self.remove_label("clarified")
                self.resume_after_clarification()
            else:
                log(f"ignored label {self.p_label}")
        else:
            log(f"ignored {event}")

    # ---- agent results
    def fresh(self, res: dict) -> bool:
        """True when the agent's latest result is at least as new as the run that just finished."""
        return result_created(res) >= self.run_started_at

    def handle_agent(self) -> None:
        if self.conclusion != "success":
            self.fail_stage(f"The `{self.agent}` run ended with `{self.conclusion}`.")
            return
        res = self.latest_result(self.agent)
        if res is None or not self.fresh(res):
            self.fail_stage(f"The `{self.agent}` run produced no result comment.")
            return
        status = result_status(res)
        log(f"{self.agent} -> {status}")
        key = f"{self.agent}/{status}"

        if key == "ba/complete":
            self.set_state("planning")
            self.dispatch("forge-planner", self.issue)
        elif key == "planner/ready-for-review":
            pr = self.plan_pr()
            if not pr:
                self.fail_stage(f"The planner reported a plan but no pull request exists for `forge/issue-{self.issue}`.")
                return
            self.remove_label("ready-for-dev")
            self.set_state("spec-review")
        elif key == "coder/ready-for-review":
            self.start_review(self.plan_pr())
        elif key == "reviewer/pass":
            self.set_state("needs-review")
        elif key == "reviewer/changes-requested":
            pr = self.plan_pr()
            self.remove_label("ready-for-dev")
            self.set_state("planning")
            self.dispatch("forge-planner", pr, "-f", f"pr={pr}", "-f", f"ref=forge/issue-{self.issue}")
        elif key in ("ba/needs-clarification", "planner/needs-clarification"):
            self.set_state("needs-clarification")
            self.comment("**Questions above.** Answer them in a comment, then add the label `clarified` to continue.")
        elif status == "blocked":
            self.fail_stage(f"The `{self.agent}` agent reported that it is blocked. Its result comment explains why.")
        else:
            self.fail_stage(f"The `{self.agent}` agent reported an unknown status `{status}`.")

    # ---- waiting for the agent runs (ADR-0018)
    def find_run(self, agent: str, since: str) -> str:
        """The run a dispatch started, found by workflow file, creation time and the run-name the agent workflows set
        (`forge-<agent>-claude OWNER/REPO#NUMBER`). The title is built from the validated target and issue and a
        fixed agent name, and a clock margin covers the difference between the runner's clock and GitHub's."""
        if not (re.fullmatch(r"[A-Za-z0-9._-]+/[A-Za-z0-9._-]+", self.target) and re.fullmatch(r"[0-9]+", self.issue)
                and re.fullmatch(r"[a-z]+", agent)):
            return ""
        try:
            t = datetime.datetime.strptime(since, "%Y-%m-%dT%H:%M:%SZ") - datetime.timedelta(seconds=30)
        except ValueError:
            return ""
        margin = t.strftime("%Y-%m-%dT%H:%M:%SZ")
        flt = (f'[.workflow_runs[] | select(.display_title == "forge-{agent}-claude {self.target}#{self.issue}" '
               f'and .created_at >= "{margin}")] | sort_by(.created_at) | last | .id // empty')
        return self.eng("api", f"repos/{self.engine_repo}/actions/workflows/forge-{agent}-claude.yml/runs"
                        "?event=workflow_dispatch&per_page=30", "--jq", flt, check=False).strip()

    def await_run(self, agent: str, since: str) -> int:
        """0 when the run completed (and sets agent, conclusion, run_started_at), 1 when no run appeared, 2 when it
        did not complete in time, 3 when the token budget ran out first."""
        interval = float(self.env.get("AWAIT_INTERVAL", "20"))
        start = time.monotonic()
        run_id = ""
        for _ in range(int(self.env.get("AWAIT_FIND_TRIES", "9"))):
            run_id = self.find_run(agent, since)
            if run_id:
                break
            time.sleep(interval)
        if not run_id:
            return 1
        log(f"waiting for {agent} run {run_id}")
        status = conclusion = started = ""
        while True:
            line = self.eng("api", f"repos/{self.engine_repo}/actions/runs/{run_id}", "--jq",
                            '.status + " " + (.conclusion // "-") + " " + (.run_started_at // "-")', check=False).strip()
            parts = line.split()
            status, conclusion, started = (parts + ["", "", ""])[:3]
            if status == "completed":
                break
            if self.waited + int(time.monotonic() - start) >= int(self.env.get("AWAIT_MAX", "5400")):
                return 2
            if int(time.monotonic() - self.started) >= self.token_budget():
                return 3
            time.sleep(interval)
        self.agent = agent
        self.conclusion = "" if conclusion == "-" else conclusion
        self.run_started_at = since if started in ("-", "") else started
        log(f"{agent} run {run_id} completed: {self.conclusion or 'unknown'}")
        return 0

    def drive(self) -> None:
        """Follows the agents this orchestration dispatched, one after the other, until nothing is pending."""
        if self.env.get("FORGE_AWAIT", "0") != "1":
            return
        stages, limit = self.stages_done, int(self.env.get("FORGE_MAX_STAGES", "12"))
        while self.pending_agent:
            agent, since = self.pending_agent, self.pending_since
            self.pending_agent = ""
            stages += 1
            self.wait_started = time.monotonic()
            if stages > limit:
                self.fail_stage(f"This request went through too many stages in one orchestration (limit {limit}).")
                return
            rc = self.await_run(agent, since)
            if rc == 0:
                self.handle_agent()
            elif rc == 1:
                self.fail_stage(f"The `{agent}` run did not start: no run titled `forge-{agent}-claude "
                                f"{self.target}#{self.issue}` appeared after the dispatch.")
                return
            elif rc == 2:
                self.fail_stage(f"The `{agent}` run did not finish within {self.env.get('AWAIT_MAX', '5400')} seconds.")
                return
            else:
                self.hand_off(agent, since, stages)
                return
            self.waited = 0

    def token_budget(self) -> int:
        """FORGE_TOKEN_BUDGET in seconds, empty or malformed means the default. Never above 3300: the token lives an
        hour, and a larger value would let it expire before the hand-off."""
        raw = (self.env.get("FORGE_TOKEN_BUDGET") or "").strip()
        return min(int(raw), 3300) if re.fullmatch(r"[0-9]+", raw) else 3300

    def hand_off(self, agent: str, since: str, stages: int) -> None:
        """The target token is about to expire while an agent still runs (ADR-0028). A new orchestrator run gets a fresh
        token and carries on with the same wait: same agent, same dispatch time, the stages and seconds already spent,
        so AWAIT_MAX and FORGE_MAX_STAGES still bound the whole request."""
        waited = self.waited + int(time.monotonic() - self.wait_started)
        log(f"handing off the wait for {agent} after {waited}s")
        try:
            self.eng("workflow", "run", "forge-orchestrator.yml", "--repo", self.engine_repo, "--ref", self.engine_ref,
                     "-f", f"target={self.target}", "-f", f"issue={self.issue}", "-f", f"agent={agent}",
                     "-f", f"since={since}", "-f", f"stages={stages - 1}", "-f", f"waited={waited}")
        except RuntimeError:
            self.fail_stage(f"The `{agent}` run was still going when the orchestrator's token was about to expire, and "
                            f"a new orchestrator run could not be started to keep waiting. See the `{agent}` run in the "
                            "engine's Actions tab, then start the request again.")

    def resume(self) -> None:
        """A handed-off wait: no event to handle, only the agent run to keep waiting for."""
        agent, since = self.env.get("P_AGENT", ""), self.env.get("P_SINCE", "")
        if agent not in AGENT_TITLES or not re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z", since):
            log("bad resume inputs")
            return
        self.pending_agent, self.pending_since = agent, since
        # The carried seconds miss the time between the hand-off and this run's start (queued behind the run that
        # handed off). The wall clock since the dispatch covers it, so AWAIT_MAX stays a bound on the whole wait.
        since_dt = datetime.datetime.strptime(since, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=datetime.timezone.utc)
        now = self.env.get("FORGE_NOW")
        now_dt = (datetime.datetime.strptime(now, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=datetime.timezone.utc) if now
                  else datetime.datetime.now(datetime.timezone.utc))
        self.waited = max(self.waited, int((now_dt - since_dt).total_seconds()))

    def main(self) -> int:
        if self.kind == "human":
            self.handle_human()
        elif self.kind == "agent":
            self.handle_agent()
        elif self.kind == "resume":
            self.resume()
        else:
            log(f"unknown kind {self.kind}")
            return 1
        self.drive()
        return 0


def main() -> int:
    try:
        return Orchestrator().main()
    except (KeyError, RuntimeError, OSError, ValueError) as e:
        log(f"error: {e!r}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
