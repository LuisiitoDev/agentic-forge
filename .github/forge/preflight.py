#!/usr/bin/env python3
"""Pre-flight check for the Forge engine and one target (docs/first-run-checklist.md). It only reads: it changes
nothing and prints no secret. One table row per check: PASS, WARN, FAIL or INFO. Exit code 1 if any check failed.

Environment (set by .github/workflows/forge-preflight.yml):
  TARGET            owner/name of the target to check
  ENGINE_REPO       this repository, owner/name
  ENGINE_TOKEN      the workflow's own token
  TARGET_TOKEN      the App installation token for TARGET; empty if it could not be created
  APP_SLUG          slug of the App, from the token step; may be empty
  ACTOR             who started the check (used to try the collaborator-permission lookup)
  ALLOWED           the value of FORGE_ALLOWED_TARGETS
  BOT_LOGIN         the value of FORGE_BOT_LOGIN
  APP_ID            the value of FORGE_APP_ID
  HAS_APP_KEY, HAS_CLAUDE   "1" when the secrets FORGE_APP_KEY and CLAUDE_CODE_OAUTH_TOKEN exist, else empty
  TARGETS_FILE      path of the engine's .github/forge/targets.txt (default: the one next to this script)
"""
import base64
import os
import re
import subprocess
import sys

REPO_RE = r"[A-Za-z0-9._-]+/[A-Za-z0-9._-]+"
WORKFLOWS = ("forge-ba-claude", "forge-planner-claude", "forge-coder-claude", "forge-reviewer-claude",
             "forge-orchestrator", "forge-intake")


class Preflight:
    def __init__(self, env):
        self.e = env
        self.fails = 0
        self.warns = 0

    def g(self, k):
        return self.e.get(k, "")

    def row(self, status, check, detail):
        print(f"| {status} | {check} | {detail} |")
        if status == "FAIL":
            self.fails += 1
        elif status == "WARN":
            self.warns += 1

    def gh(self, token, *args):
        """stdout of gh, or None when it fails."""
        r = subprocess.run(["gh", *args], capture_output=True, text=True, env=dict(self.e, GH_TOKEN=token))
        return r.stdout if r.returncode == 0 else None

    def tgt(self, *args):
        return self.gh(self.g("TARGET_TOKEN"), *args)

    def eng(self, *args):
        return self.gh(self.g("ENGINE_TOKEN"), *args)

    @staticmethod
    def decode(out):
        try:
            return base64.b64decode((out or "").strip()).decode("utf-8") if (out or "").strip() else ""
        except (ValueError, UnicodeDecodeError):
            return ""

    def file_of(self, path):
        """The decoded content of a file of the target, empty if it does not exist."""
        return self.decode(self.tgt("api", f"repos/{self.g('TARGET')}/contents/{path}", "--jq", ".content"))

    @staticmethod
    def in_list(needle, text):
        """Whole entry, case-insensitive; entries separated by commas or newlines, `#` comments."""
        items = []
        for line in text.split("\n"):
            line = line.split("#", 1)[0]
            items += [re.sub(r"[ \t]", "", x) for x in line.split(",")]
        return needle.lower() in [i.lower() for i in items if i]

    def summary(self):
        print()
        print(f"**{self.fails} failed, {self.warns} warnings.**")
        return 0 if self.fails == 0 else 1

    def run(self) -> int:
        target = self.g("TARGET")
        print(f"## Forge pre-flight for `{target or '?'}`")
        print()
        print("| Result | Check | Detail |")
        print("| --- | --- | --- |")
        if not re.fullmatch(REPO_RE, target):
            self.row("FAIL", "target", "`TARGET` must be owner/name")
            self.summary()
            return 1
        engine = self.e["ENGINE_REPO"]
        targets_file = self.g("TARGETS_FILE") or os.path.join(os.path.dirname(os.path.abspath(__file__)), "targets.txt")
        bot = self.g("BOT_LOGIN")

        # 1. Engine configuration. Secrets are only reported as present or missing.
        r = self.row
        r("PASS", "FORGE_APP_ID", "set") if self.g("APP_ID") else r("FAIL", "FORGE_APP_ID", "variable is empty")
        r("PASS", "FORGE_APP_KEY", "secret exists") if self.g("HAS_APP_KEY") else r("FAIL", "FORGE_APP_KEY", "secret is missing")
        if self.g("HAS_CLAUDE"):
            r("PASS", "CLAUDE_CODE_OAUTH_TOKEN", "secret exists")
        else:
            r("FAIL", "CLAUDE_CODE_OAUTH_TOKEN", "secret is missing: every agent runs on Claude Code, so every run would fail")
        if bot:
            r("PASS", "FORGE_BOT_LOGIN", f"`{bot}`")
        else:
            r("FAIL", "FORGE_BOT_LOGIN", "variable is empty: no agent result would be trusted")

        # 2. The two allowlists must both name the target.
        if self.in_list(target, self.g("ALLOWED")):
            r("PASS", "FORGE_ALLOWED_TARGETS", "lists the target")
        else:
            r("FAIL", "FORGE_ALLOWED_TARGETS", f"does not list `{target}`: the orchestrator would skip every event")
        try:
            with open(targets_file) as f:
                listed = f.read()
        except OSError:
            listed = ""
        if self.in_list(target, listed):
            r("PASS", "targets.txt", "lists the target (on the branch this check runs on)")
        else:
            r("FAIL", "targets.txt", f"does not list `{target}`: the intake would ignore every event")

        # 3. The App.
        if not self.g("TARGET_TOKEN"):
            r("FAIL", "App token", f"could not be created for `{target}`: the App is not installed on it, `FORGE_APP_ID` or "
              "`FORGE_APP_KEY` is wrong, or the target is not spelled as installed. Target checks below are skipped")
        else:
            r("PASS", "App token", "created for the target only")
            slug = self.g("APP_SLUG")
            if not slug:
                r("WARN", "bot login", "the App slug is unknown, so `FORGE_BOT_LOGIN` was not compared")
            elif bot.lower() == f"{slug}[bot]".lower():
                r("PASS", "bot login", "`FORGE_BOT_LOGIN` matches the App")
            else:
                r("FAIL", "bot login", f"`FORGE_BOT_LOGIN` is `{bot}` but the App is `{slug}[bot]`: its results would not be trusted")

            if self.tgt("api", f"repos/{target}", "--jq", ".full_name") is not None:
                r("PASS", "target repository", "readable with the App token")
            else:
                r("FAIL", "target repository", "not readable with the App token")

            actor = self.g("ACTOR")
            perm = (self.tgt("api", f"repos/{target}/collaborators/{actor}/permission", "--jq", ".permission")
                    if re.fullmatch(r"[A-Za-z0-9-]+", actor) else None)
            if perm and perm.strip():
                r("PASS", "permission lookup", f"`{actor}` has `{perm.strip()}` on the target: the lookup the orchestrator uses for approvals works")
            else:
                r("FAIL", "permission lookup", "the App token cannot read collaborator permissions of the target")

            labels = (self.tgt("api", f"repos/{target}/labels?per_page=100", "--paginate", "--jq", ".[].name") or "").split("\n")
            missing = "".join(f" `{x}`" for x in ("forge", "ready-for-dev", "clarified") if x not in labels)
            if not missing:
                r("PASS", "labels", "`forge`, `ready-for-dev` and `clarified` exist")
            else:
                r("INFO", "labels", f"missing:{missing}. The engine creates them when the first request starts; "
                  "create `forge` by hand to start that request")

            # No target configuration (ADR-0030): the Coder's scope is the approved plan's paths.
            caller = self.file_of(".github/workflows/forge.yml")
            if not caller:
                r("FAIL", "caller workflow", "`.github/workflows/forge.yml` is missing in the target")
            else:
                m = re.search(r"^[ \t]*uses:[ \t]*([^@\s]*)@([^\s#]*)", caller, re.M)
                cuses, cref = (m.group(1), m.group(2)) if m else ("", "")
                m = re.search(r"^[ \t]*engine-ref:[ \t]*([^\s#]*)", caller, re.M)
                eref = m.group(1) if m else ""
                if not cref or not eref:
                    r("FAIL", "caller workflow", "no `uses: ...@ref` or no `engine-ref` found")
                elif "<" in cref or "<" in eref:
                    r("FAIL", "caller workflow", "still has the `<TAG-OR-SHA>` placeholder")
                elif cref != eref:
                    r("FAIL", "caller workflow", f"`uses` is pinned to `{cref}` but `engine-ref` is `{eref}`: they must be the same")
                elif cuses.split("/.github/")[0].lower() != engine.lower():
                    r("FAIL", "caller workflow", f"calls `{cuses.split('/.github/')[0]}`, not this engine (`{engine}`)")
                elif re.fullmatch(r"[0-9a-f]{40}", cref):
                    r("PASS", "caller workflow", "pinned to a full commit SHA, both places equal")
                else:
                    r("WARN", "caller workflow", f"pinned to `{cref}`, which is not a full commit SHA: a tag can move")

                # The intake reads targets.txt at the pinned ref, so that ref must already name the target.
                if re.fullmatch(r"[A-Za-z0-9._/-]+", cref) and "<" not in cref:
                    pinned = self.decode(self.eng("api", f"repos/{engine}/contents/.github/forge/targets.txt?ref={cref}",
                                                  "--jq", ".content"))
                    if not pinned:
                        r("FAIL", "pinned ref", f"`targets.txt` cannot be read at `{cref}` in the engine")
                    elif self.in_list(target, pinned):
                        r("PASS", "pinned ref", f"`targets.txt` at `{cref}` lists the target")
                    else:
                        r("FAIL", "pinned ref", f"`targets.txt` at `{cref}` does not list `{target}`: the intake would ignore "
                          "every event. Pin a newer commit")

        # 4. The engine's workflows are registered and active.
        active = (self.eng("api", f"repos/{engine}/actions/workflows?per_page=100", "--jq",
                           '.workflows[] | select(.state == "active") | .name') or "").split("\n")
        missing = "".join(f" `{w}`" for w in WORKFLOWS if w not in active)
        if not missing:
            r("PASS", "engine workflows", "all six are active")
        else:
            r("FAIL", "engine workflows", f"not active on the default branch:{missing}")

        # 5. What cannot be checked from here.
        r("INFO", "dispatch token", "lives in the target as `FORGE_DISPATCH_TOKEN`; the first intake run is its check")
        r("INFO", "engine", "Claude Code runs every agent; whether the token is valid shows on the first agent run")
        return self.summary()


def main() -> int:
    try:
        return Preflight(dict(os.environ)).run()
    except KeyError as e:
        print(f"missing environment variable {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
