# Workflow definition format (schemaVersion 1)

A workflow is a plain JSON document. It has no GitHub Actions syntax, no vendor model names and no code.
`target` is a *logical* agent id or tool name resolved by the host at run time.

* Schema: [`schema/workflow-definition.schema.json`](schema/workflow-definition.schema.json) (JSON Schema 2020-12)
* Loader: `AgenticForge.Domain.WorkflowLoader.Load(json)` returns a definition or a list of errors; it never throws on bad input and needs no external services.
* Valid examples: [`examples/workflows/`](examples/workflows/) (`valid-minimal.json`, `valid-spec-driven-delivery.json`)
* Invalid examples: [`examples/workflows/invalid/`](examples/workflows/invalid/)

## Fields

| Field | Required | Notes |
|---|---|---|
| `schemaVersion` | yes | Must be `1`. Other values are rejected. |
| `id`, `name` | yes | `id` is shared by all versions of the workflow. |
| `version` | yes | `major.minor.patch`; a run is pinned to one version. |
| `start` | yes | Id of the first step. |
| `steps[]` | yes (>= 1) | `id`, `kind` (`Agent` \| `Tool` \| `Approval`), `target`, `capabilities`, `allowedOutcomes`, `retry` (`maxAttempts` >= 1, `delay`), `timeout` (`hh:mm:ss`). |
| `transitions[]` | yes | `from`, `outcome`, optional `to`. Omitting `to` completes the run successfully. |

Step kinds: `Agent` and `Tool` require a `target`; `Approval` forbids `target` and `capabilities`.

**Failure behavior (v1):** a step failure that exhausts its bounded retries fails the run. There are no failure
transitions. Cancellation is not a failure.

**Inputs:** not part of schemaVersion 1. The domain contract (#19) has no workflow inputs; adding them is an
additive, optional change (see below) and is left for a later issue.

## Validation

1. **Schema** (structural): required fields, types, kind enum, version/duration formats, `retry.maxAttempts >= 1`, target rules per kind.
2. **Loader** (before schema-level semantics): well-formed JSON, object root, required properties, `schemaVersion` supported.
3. **Semantic** (`WorkflowDefinition.Validate`): duplicate step ids; undefined start; transitions from/to unknown steps;
   transition outcome not in a step's `allowedOutcomes`; allowed outcome without transition; ambiguous (duplicate)
   `from`+`outcome`; steps unreachable from `start`; blank/duplicate/padded capability and outcome names;
   non-positive timeouts and negative retry delays.

Cycles are **allowed** (e.g. review -> implement loops); termination is bounded by step retry and run-level policy in the runtime (#7).
The JSON Schema cannot express cross-references, so it is necessary but not sufficient: always use the loader.

## Evolution

Follows [compatibility-and-versioning.md](compatibility-and-versioning.md): additive optional properties do not bump
`schemaVersion` and readers ignore unknown properties (the schema permits additional properties for this reason);
removing/renaming properties or enum values, or making an optional property required, bumps `schemaVersion` to 2 and
the schema `$id` to `.../2.json`. Workflow authors bump `version` per the patch/minor/major rules there.

## Example

See [`examples/workflows/valid-spec-driven-delivery.json`](examples/workflows/valid-spec-driven-delivery.json): analyze (agent) -> approve-plan (approval) -> implement (agent) -> review (agent), looping back to implement on `changes-requested`.
