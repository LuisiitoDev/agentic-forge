# Compatibility and versioning rules

Applies to all serialized contracts (definitions, run/step snapshots, invocation requests/results) and the
public C# surface of `AgenticForge.Domain`, `.Abstractions`, `.Application`.

## Two independent version axes

1. **Contract schema version** (`schemaVersion`, integer; `ContractSchema.Current` = 1). Describes the *shape* of
   the serialized contract. Present on `WorkflowDefinition`, `WorkflowRunSnapshot`, `StepRunSnapshot`.
2. **Workflow version** (`major.minor.patch`). Describes one author's *workflow*. A `WorkflowRun` is pinned to
   one workflow id + version for its whole life; editing a definition means publishing a new version, never
   mutating one in use.

## Schema rules

* **Additive changes are compatible** and do not bump `schemaVersion`: new optional properties, new optional
  members of records, new enum values that old readers can treat as unknown. Optional means nullable or defaulted.
* **Readers must ignore unknown properties** (System.Text.Json default; covered by a test) and must not fail on
  unknown `Failure.Details` keys.
* **Breaking changes bump `schemaVersion`:** removing/renaming a property or enum value, changing a type or
  meaning, making an optional property required, changing identifier formats.
* A reader that sees a `schemaVersion` higher than it supports must reject explicitly, not guess.
* After a bump, the runtime supports reading the previous version for at least one minor release and upgrades on
  read (never rewrites history in place).
* Enums serialize as PascalCase strings; **new `StepKind`/`InvocationStatus`/`FailureKind` values are
  additive for writers but require consumers to handle unknown values** (treat as failure with `Validation`).
* Property names are camelCase; absent and `null` are equivalent; nulls are omitted on write (a `Transition`
  without `to` completes the run).
* Identifiers are opaque non-empty strings; consumers must not parse them.

## Workflow version rules

* **patch:** no behavior change (name, descriptions).
* **minor:** compatible behavior change, e.g. added step or outcome that existing runs never reach.
* **major:** removed/renamed steps or outcomes, changed step targets or policies.
* In-flight runs always finish on their pinned version.

## C# API rules

* Types in Domain/Abstractions/Application are the public API; everything else is `internal`.
* Records gain members only as optional trailing parameters with defaults. Interface members are not added to
  published ports in a minor version; new capabilities arrive as new interfaces (e.g. `IAgent` stays stable).
* Pre-1.0 (current): breaking C# changes are allowed but must update the examples and tests in the same PR.

## Enforcement

`tests/AgenticForge.Contracts.Tests/JsonExampleTests.cs` round-trips every file in `docs/design/examples/`.
A change that alters a serialized shape fails these tests, forcing an explicit decision: update the example
(compatible change) or bump `schemaVersion` (breaking change).
