# ADR-0027: A deterministic model router per agent

**Status:** Accepted. Extends [ADR-0022](0022-all-agents-on-claude-in-awf.md) and [ADR-0024](0024-claude-code-only.md).

## Context

Every agent ran on the CLI's default model, so the cheapest task (a BA reading a request) and the hardest (a Coder
changing a sensitive path) got the same model. The engine has one subscription and three models: Haiku, Sonnet and
Opus.

GitHub's Copilot Fusion picks a model per prompt at runtime, and in some modes a model drafts and another judges. We
take only the idea of choosing by need. We do not copy the mechanism: a model that decides the model adds cost and
latency, and the request author could steer it. Cascade and critique would double the calls per task.

## Decision

1. **A fixed table per agent** (`choose_model.py`): BA haiku; Planner, Coder and Reviewer sonnet.
2. **Move up only on signals the engine owns**, read from trusted results and the plan, never from the request:
   - Coder to opus when the plan lists `tasks.app` of 6 or more (first estimate), touches a sensitive path, or is a replan.
   - Planner to opus on a replan (the Reviewer asked for changes).
   - Reviewer to opus when the plan touches a sensitive path.
3. **A pin per agent** through the repository variable `FORGE_MODEL_<AGENT>` (`haiku`, `sonnet` or `opus`). An unknown
   value fails the step; it never falls back silently.
4. The chosen model is passed to Claude Code with `--model`. `run_claude.py` refuses any model id outside the three.

## Consequences

- The thresholds are a starting point, to be tuned with real runs. The table and the signals are tested in
  `test_choose_model.py`.
- If the subscription does not include a model, its runs fail; the pin is the way out, and the step fails instead of
  degrading silently.
- No model call happens to choose a model: the cost of the decision is zero and it is reproducible.
