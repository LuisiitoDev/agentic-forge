#!/usr/bin/env python3
"""Deterministic defenses against prompt injection (ADR-0026). No model is involved, so none of this can be talked out of
its job. Four jobs:

  clean_untrusted   text that a person wrote, before an agent reads it: no hidden HTML comments, no invisible or
                    bidirectional control characters (the usual way to hide an instruction from the human reviewer)
  find_secrets      credentials in what an agent produced (its result, its patch), before anything is published
  neutralize_result what the engine posts as a comment: no image or HTML that fetches a URL when someone opens the
                    page (a way to leak data), and no @mention that notifies a stranger
  risky_files       files whose change deserves a human look whatever the plan said (dependencies, build, CI, runtime)
"""
import re

# Zero-width and bidirectional controls, the BOM, and the Unicode "tag" block that can spell hidden ASCII.
_INVISIBLE = re.compile("[​-‏‪-‮⁠-⁤⁦-⁩﻿\U000e0000-\U000e007f]")
_HTML_COMMENT = re.compile(r"<!--.*?-->", re.S)

_SECRET_PATTERNS = (
    ("an Anthropic key or token", re.compile(r"sk-ant-[A-Za-z0-9_-]{20,}")),
    ("a GitHub token", re.compile(r"\bgh[pousr]_[A-Za-z0-9]{30,}")),
    ("a GitHub token", re.compile(r"\bgithub_pat_[A-Za-z0-9_]{30,}")),
    ("a private key", re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
    ("an AWS access key", re.compile(r"\bAKIA[0-9A-Z]{16}\b")),
    ("a Slack token", re.compile(r"\bxox[abprs]-[A-Za-z0-9-]{20,}")),
)

_RISKY = (
    re.compile(r"(^|/)(package(-lock)?\.json|yarn\.lock|pnpm-lock\.yaml|npm-shrinkwrap\.json|\.npmrc|\.yarnrc(\.yml)?)$"),
    re.compile(r"(^|/)(requirements[^/]*\.txt|Pipfile(\.lock)?|pyproject\.toml|poetry\.lock|setup\.py|setup\.cfg|tox\.ini)$"),
    re.compile(r"(^|/)(NuGet\.config|Directory\.(Build|Packages)\.(props|targets)|global\.json|packages\.lock\.json)$"),
    re.compile(r"\.(csproj|vbproj|fsproj|sln|props|targets)$"),
    re.compile(r"(^|/)(Dockerfile[^/]*|docker-compose[^/]*\.ya?ml|Makefile|makefile|Jenkinsfile|\.gitlab-ci\.yml|azure-pipelines\.ya?ml)$"),
    re.compile(r"(^|/)(go\.(mod|sum)|Cargo\.(toml|lock)|pom\.xml|build\.gradle(\.kts)?|Gemfile(\.lock)?|composer\.(json|lock))$"),
    re.compile(r"(^|/)(\.husky|\.githooks|\.devcontainer|\.vscode)/"),
    re.compile(r"(^|/)(CODEOWNERS|\.gitmodules|\.gitattributes)$"),
    re.compile(r"\.(sh|bash|ps1|bat|cmd)$"),
)


def clean_untrusted(text):
    """The text as a person would see it rendered: HTML comments and invisible characters are gone."""
    if not isinstance(text, str):
        return text
    return _INVISIBLE.sub("", _HTML_COMMENT.sub("", text))


def clean_tree(value):
    """clean_untrusted on every string of a JSON value (dicts, lists, strings)."""
    if isinstance(value, str):
        return clean_untrusted(value)
    if isinstance(value, list):
        return [clean_tree(v) for v in value]
    if isinstance(value, dict):
        return {k: clean_tree(v) for k, v in value.items()}
    return value


def has_invisible(text) -> bool:
    return bool(_INVISIBLE.search(text or ""))


def find_secrets(text, literals=()):
    """Names of the kinds of secret found (never the value). `literals` are exact values the engine itself holds."""
    found = []
    for name, pattern in _SECRET_PATTERNS:
        if pattern.search(text or "") and name not in found:
            found.append(name)
    for value in literals:
        if value and len(value) >= 8 and value in (text or "") and "a token the engine holds" not in found:
            found.append("a token the engine holds")
    return found


_IMAGE = re.compile(r"!\[([^\]]*)\]\([^)]*\)")
_HTML_TAG = re.compile(r"</?\s*(img|a|iframe|script|style|link|object|embed|svg|video|audio|source|form|input|meta|base)\b[^>]*>", re.I)
_MENTION = re.compile(r"(?<![\w`])@(?=[A-Za-z0-9])")


def neutralize_result(text: str) -> str:
    """What the engine posts. Images and fetching HTML are removed (the page would request the URL, with whatever the
    agent put in it, as soon as anyone opens it), and @mentions are broken so the agent cannot notify people."""
    text = _IMAGE.sub(lambda m: f"[image removed: {m.group(1) or 'no description'}]", text)
    text = _HTML_TAG.sub("", text)
    return _MENTION.sub("@​", text)


def risky_files(paths):
    """The paths a human should look at twice: dependencies, build and CI definitions, hooks, scripts."""
    return [p for p in paths if p and any(r.search(p) for r in _RISKY)]
