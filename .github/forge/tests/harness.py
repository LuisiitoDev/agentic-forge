"""Shared helpers for the tests that run a script against the fake `gh` in tests/bin."""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
FORGE = os.path.join(ROOT, ".github", "forge")
BIN = os.path.join(HERE, "bin")

BOT = "forge[bot]"


def cm(body, created_at, user=BOT):
    return {"body": body, "created_at": created_at, "user": {"login": user}}


def result(agent, status, app=1, infra=0):
    title = "BA" if agent == "ba" else agent.capitalize()
    paths = "paths:\n  - src/a.cs\n" if agent == "planner" else ""  # a plan declares its paths (ADR-0030)
    return f"## {title} result: {status}\n\n```yaml\nstatus: {status}\ntasks:\n  app: {app}\n  infra: {infra}\n{paths}```\n"


def planp(*paths):
    """A planner result whose YAML lists those paths."""
    out = "## Planner result: ready-for-review\n\n```yaml\nstatus: ready-for-review\ntasks:\n  app: 1\n  infra: 0\npaths:\n"
    return out + "".join(f"  - {p}\n" for p in paths) + "```\n"


class FakeHub(unittest.TestCase):
    """A temp state directory for the fake gh, and a way to run a script with it on the PATH."""

    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir, True)

    def reset(self):
        shutil.rmtree(self.dir, True)
        os.makedirs(self.dir)
        for name in ("calls.log", "tokens.log", "dispatch.log", "posted.txt", "labels.txt"):
            open(f"{self.dir}/{name}", "w").close()

    def write(self, name, text):
        path = f"{self.dir}/{name}"
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w") as f:
            f.write(text if isinstance(text, str) else json.dumps(text))

    def read(self, name):
        try:
            with open(f"{self.dir}/{name}") as f:
                return f.read()
        except OSError:
            return ""

    def env(self, **extra):
        e = {k: v for k, v in os.environ.items() if not k.startswith(("P_", "FORGE_", "AWAIT_"))}
        e.update(PATH=BIN + os.pathsep + e["PATH"], FAKE_DIR=self.dir)
        e.update({k: str(v) for k, v in extra.items()})
        return e

    def script(self, path, env, stdin=None):
        return subprocess.run([sys.executable, path], env=env, input=stdin, capture_output=True, text=True)

    def unhandled(self):
        return self.read("unhandled.log")


ORCH_ENV = dict(TARGET="o/r", ISSUE="12", TARGET_TOKEN="t", ENGINE_TOKEN="e", ENGINE_REPO="eng/forge",
                ENGINE_REF="main", FORGE_BOT_LOGIN=BOT, RUN_URL="http://run")


class OrchestratorHub(FakeHub):
    """Runs orchestrate.py on a scenario and checks what it dispatched, labeled and posted."""

    def orchestrate(self, labels="", comments=None, pulls=None, pr_comments=None, files=None, setup=None, **env):
        self.reset()
        self.write("labels.txt", "".join(f"{x}\n" for x in labels.split()))
        self.write("comments.json", comments or [])
        self.write("pulls.json", pulls or [])
        if pr_comments is not None:
            self.write("pr_comments.json", pr_comments)
        self.write("files.json", files or [])
        if setup:
            setup()
        r = self.script(os.path.join(FORGE, "orchestrate.py"), self.env(**ORCH_ENV, **env))
        self.stderr = r.stderr
        return r

    def state(self):
        return (self.read("dispatch.log"), " ".join(sorted(self.read("labels.txt").split())), self.read("posted.txt"))
