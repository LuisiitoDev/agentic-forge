using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticForge.Abstractions;
using AgenticForge.Application;
using AgenticForge.Domain;

namespace AgenticForge.Runtime;

/// <summary>
/// Deterministic, provider-neutral workflow engine. The engine alone moves a run between steps and between
/// states: agents and tools only return results, which are validated here against the workflow definition.
/// Execution is sequential (one active step per run) and a call returns when the run is terminal or paused.
/// Runs persist after every transition through <see cref="IRunStore"/>, and a transition is acknowledged only after its save
/// returned. Interrupted runs are continued by <see cref="IRunRecovery"/>; see docs/design/persistence-and-recovery.md.
/// </summary>
public sealed partial class WorkflowOrchestrator : IWorkflowOrchestrator, IRunRecovery
{
    private readonly IWorkflowCatalog _catalog;
    private readonly IRunStore _store;
    private readonly IAgentResolver _agents;
    private readonly IToolResolver _tools;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly RuntimeOptions _options;
    private readonly IExecutionEventSink _events;

    public WorkflowOrchestrator(IWorkflowCatalog catalog, IRunStore store, IAgentResolver agents, IToolResolver tools,
        IIdGenerator? ids = null, TimeProvider? time = null, RuntimeOptions? options = null, IExecutionEventSink? events = null)
    {
        _catalog = catalog;
        _store = store;
        _agents = agents;
        _tools = tools;
        _ids = ids ?? new GuidIdGenerator();
        _time = time ?? TimeProvider.System;
        _options = options ?? new RuntimeOptions();
        _events = events ?? NullEventSink.Instance;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxStepRuns, 1);
    }

    // ---------------------------------------------------------------- public API

    /// <remarks>
    /// A start with an <c>IdempotencyKey</c> already used for the workflow returns the existing run unchanged (whatever its
    /// state) and never executes anything; the other fields of the repeated command are ignored.
    /// </remarks>
    public async ValueTask<WorkflowRunSnapshot> StartAsync(StartRunCommand command, CancellationToken cancellationToken)
    {
        if (command.IdempotencyKey is { } existingKey
            && await _store.FindRunByIdempotencyKeyAsync(command.Workflow, existingKey, cancellationToken) is { } existing)
            return existing;

        var def = await _catalog.FindAsync(command.Workflow, command.Version, cancellationToken)
                  ?? throw new WorkflowNotFoundException(command.Workflow, command.Version);
        var errors = def.Validate();
        if (errors.Count > 0) throw new InvalidWorkflowException(errors);

        var runId = new WorkflowRunId(_ids.NewId("run"));
        var run = WorkflowRun.Create(runId, def, command.ExternalReference ?? runId.Value, Now, command.Input, command.IdempotencyKey);
        try
        {
            await SaveAsync(run);
        }
        catch (DuplicateIdempotencyKeyException lost)
        {
            // A concurrent start with the same key won the race; return its run instead of creating a second one.
            return await _store.GetRunAsync(lost.ExistingRun, CancellationToken.None)
                   ?? throw new InvalidOperationException("The run holding the idempotency key disappeared.", lost);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            run.Cancel(Now, "Cancelled before start.");
            await SaveAsync(run);
            await EmitAsync(run, ExecutionEventKind.RunCancelled);
            return run.State;
        }

        run.Start(Now, def.Start);
        await SaveAsync(run);
        await EmitAsync(run, ExecutionEventKind.RunStarted);
        return await DriveAsync(run, def, null, null, null, cancellationToken);
    }

    /// <remarks>A cancelled token throws <see cref="OperationCanceledException"/> before any state change; use <see cref="CancelAsync"/> to cancel the run.</remarks>
    public async ValueTask<WorkflowRunSnapshot> ResumeAsync(ResumeRunCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await _store.GetRunAsync(command.Run, cancellationToken)
                       ?? throw new KeyNotFoundException($"Run '{command.Run}' was not found.");
        var run = WorkflowRun.Rehydrate(snapshot);
        if (run.Status != WorkflowRunStatus.Paused)
            throw new InvalidLifecycleTransitionException(nameof(WorkflowRun), run.Status.ToString(), "resume");

        var def = await _catalog.FindAsync(snapshot.WorkflowId, snapshot.WorkflowVersion, cancellationToken)
                  ?? throw new WorkflowNotFoundException(snapshot.WorkflowId, snapshot.WorkflowVersion);
        var stepDef = StepOf(def, snapshot.CurrentStep!.Value);

        // v1 runs one step at a time, so the waiting step run is the latest one for the current step.
        var waiting = (await _store.GetStepRunsAsync(run.Id, cancellationToken))
            .LastOrDefault(s => s.StepId == stepDef.Id && s.Status is StepRunStatus.AwaitingInput or StepRunStatus.AwaitingApproval);
        if (waiting is null)
            throw new InvalidLifecycleTransitionException(nameof(WorkflowRun), run.Status.ToString(), "resume (no waiting step)");
        var step = StepRun.Rehydrate(waiting);

        if (waiting.Status == StepRunStatus.AwaitingApproval)
        {
            if (command.Decision is not { } decision)
                throw new InvalidLifecycleTransitionException(nameof(StepRun), waiting.Status.ToString(), "resume with input");
            step.Resume(decision);
            run.Resume();
            var outcome = RuntimeOptions.OutcomeFor(decision.Decision);
            StepResult result;
            if (CheckOutcome(def, stepDef, outcome) is { } bad)
            {
                step.Fail(Now, bad);
                result = StepResult.Failed(bad);
            }
            else
            {
                step.Succeed(Now, outcome);
                result = StepResult.Done(outcome, null, null, step.Id);
            }
            await SaveAsync(step);
            await SaveAsync(run);
            await EmitAsync(run, ExecutionEventKind.ApprovalDecided, step, outcome: outcome, actor: decision.Approver);
            await EmitAsync(run, ExecutionEventKind.RunResumed, step);
            await EmitStepEndAsync(run, step);
            return await DriveAsync(run, def, result, null, null, cancellationToken);
        }

        if (command.Input is not { } answer)
            throw new InvalidLifecycleTransitionException(nameof(StepRun), waiting.Status.ToString(), "resume with a decision");
        step.Resume();
        run.Resume();
        await SaveAsync(step);
        await SaveAsync(run);
        await EmitAsync(run, ExecutionEventKind.RunResumed, step);
        return await DriveAsync(run, def, null, step, answer, cancellationToken);
    }

    /// <summary>Cancels a non-terminal run and its active step run. Cancelling a terminal run is an invalid transition.</summary>
    public async ValueTask<WorkflowRunSnapshot> CancelAsync(WorkflowRunId runId, string? reason, CancellationToken cancellationToken)
    {
        var snapshot = await _store.GetRunAsync(runId, cancellationToken)
                       ?? throw new KeyNotFoundException($"Run '{runId}' was not found.");
        var run = WorkflowRun.Rehydrate(snapshot);
        run.Cancel(Now, reason); // throws if terminal, before anything is touched
        foreach (var s in await _store.GetStepRunsAsync(runId, cancellationToken))
        {
            if (s.Status is StepRunStatus.Succeeded or StepRunStatus.Failed or StepRunStatus.Cancelled) continue;
            var step = StepRun.Rehydrate(s);
            step.Cancel(Now, reason);
            await SaveAsync(step);
            await EmitStepEndAsync(run, step);
        }
        await SaveAsync(run);
        await EmitAsync(run, ExecutionEventKind.RunCancelled);
        return run.State;
    }

    // ---------------------------------------------------------------- run loop

    private async ValueTask<WorkflowRunSnapshot> DriveAsync(WorkflowRun run, WorkflowDefinition def,
        StepResult? first, StepRun? resumed, JsonElement? answer, CancellationToken ct, int firstAttempt = 1)
    {
        var pending = first;
        while (true)
        {
            var stepDef = StepOf(def, run.State.CurrentStep!.Value);
            StepResult result;
            if (pending is { } p)
            {
                result = p;
                pending = null;
            }
            else
            {
                if (ct.IsCancellationRequested) return await CancelRunAsync(run, resumed);
                if (resumed is null && (await _store.GetStepRunsAsync(run.Id, CancellationToken.None)).Count >= _options.MaxStepRuns)
                    return await FailRunAsync(run, new Failure(FailureKind.Validation, "step_budget_exceeded",
                        $"The run exceeded its budget of {_options.MaxStepRuns} step runs."));
                result = await ExecuteStepAsync(run, def, stepDef, resumed, answer, ct, firstAttempt);
                resumed = null;
                answer = null;
            }

            switch (result.Kind)
            {
                case StepResultKind.Waiting:
                    run.Pause();
                    await SaveAsync(run);
                    await CheckpointAsync(run, CheckpointKind.Waiting, stepDef.Id, result.StepRun);
                    await EmitAsync(run, ExecutionEventKind.RunPaused);
                    return run.State;
                case StepResultKind.Failed:
                    return await FailRunAsync(run, result.Failure!);
                case StepResultKind.Cancelled:
                    return await CancelRunAsync(run, null, result.Reason);
                default:
                    // A step with no outgoing transitions is terminal: the run succeeds with that step's output.
                    var next = def.Transitions.FirstOrDefault(t => t.From == stepDef.Id && t.Outcome == result.Outcome)?.To;
                    if (next is null)
                    {
                        run.Succeed(Now, result.Output, result.Artifacts);
                        await SaveAsync(run);
                        await CheckpointAsync(run, CheckpointKind.StepCompleted, stepDef.Id, result.StepRun, result.Outcome, null, result.Artifacts);
                        await EmitAsync(run, ExecutionEventKind.RunSucceeded, outcome: result.Outcome, artifacts: result.Artifacts);
                        return run.State;
                    }
                    run.MoveTo(next.Value);
                    await SaveAsync(run);
                    await CheckpointAsync(run, CheckpointKind.StepCompleted, stepDef.Id, result.StepRun, result.Outcome, next, result.Artifacts);
                    break;
            }
        }
    }

    private async ValueTask<WorkflowRunSnapshot> FailRunAsync(WorkflowRun run, Failure failure)
    {
        run.Fail(Now, failure);
        await SaveAsync(run);
        await EmitAsync(run, ExecutionEventKind.RunFailed, failure: failure);
        return run.State;
    }

    private async ValueTask<WorkflowRunSnapshot> CancelRunAsync(WorkflowRun run, StepRun? active, string? reason = null)
    {
        reason ??= "Cancellation requested by the host.";
        if (active is { IsTerminal: false })
        {
            active.Cancel(Now, reason);
            await SaveAsync(active);
            await EmitStepEndAsync(run, active);
        }
        run.Cancel(Now, reason);
        await SaveAsync(run);
        await EmitAsync(run, ExecutionEventKind.RunCancelled);
        return run.State;
    }

    // ---------------------------------------------------------------- step execution (with bounded retry)

    private async ValueTask<StepResult> ExecuteStepAsync(WorkflowRun run, WorkflowDefinition def, StepDefinition stepDef,
        StepRun? resumed, JsonElement? answer, CancellationToken ct, int firstAttempt = 1)
    {
        var maxAttempts = stepDef.Retry?.MaxAttempts ?? 1;
        var attempt = resumed?.State.Attempt ?? firstAttempt;
        var step = resumed;
        while (true)
        {
            if (step is null)
            {
                var input = await BuildInputAsync(run);
                step = StepRun.Create(new StepRunId(_ids.NewId("step")), run.Id, stepDef.Id, attempt, input);
                await SaveAsync(step);
                step.Start(Now);
                await SaveAsync(step);
                await EmitAsync(run, ExecutionEventKind.StepStarted, step);
            }

            var result = await RunAttemptAsync(run, def, stepDef, step, answer, ct);
            answer = null;
            if (result is { Kind: StepResultKind.Failed, Failure.Retryable: true } && attempt < maxAttempts)
            {
                await EmitAsync(run, ExecutionEventKind.StepRetryScheduled, step, failure: result.Failure,
                    attributes: new Dictionary<string, string> { ["nextAttempt"] = (attempt + 1).ToString() });
                attempt++;
                step = null;
                try
                {
                    if (stepDef.Retry?.Delay is { } delay && delay > TimeSpan.Zero)
                        await Task.Delay(delay, _time, ct);
                }
                catch (OperationCanceledException)
                {
                    return StepResult.Cancelled("Cancellation requested by the host.");
                }
                continue;
            }
            return result;
        }
    }

    private async ValueTask<StepResult> RunAttemptAsync(WorkflowRun run, WorkflowDefinition def, StepDefinition stepDef,
        StepRun step, JsonElement? answer, CancellationToken ct)
    {
        if (stepDef.Kind == StepKind.Approval)
        {
            step.AwaitApproval();
            await SaveAsync(step);
            await EmitAsync(run, ExecutionEventKind.ApprovalRequested, step);
            await EmitAsync(run, ExecutionEventKind.StepAwaitingApproval, step);
            return StepResult.Waiting(step.Id);
        }

        using var timeoutCts = stepDef.Timeout is { } t ? new CancellationTokenSource(t, _time) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var context = new StepExecutionContext(run.Correlation(step.Id), step.State.Attempt,
            stepDef.Timeout is { } to ? Now + to : null);

        var isAgent = stepDef.Kind == StepKind.Agent;
        var target = new Dictionary<string, string> { [isAgent ? "agent" : "tool"] = stepDef.Target! };
        await EmitAsync(run, isAgent ? ExecutionEventKind.AgentInvoked : ExecutionEventKind.ToolInvoked, step, attributes: target);

        Invocation inv;
        try
        {
            inv = await InvokeAsync(stepDef, step, context, answer, linked.Token);
        }
        catch (OperationCanceledException)
        {
            inv = new Invocation(InvocationStatus.Cancelled);
        }
        catch (Exception)
        {
            // Never leak provider exception types or messages (they may carry secrets).
            inv = new Invocation(InvocationStatus.Failed, Failure: new Failure(FailureKind.Internal, "unexpected_exception",
                "The agent or tool threw an unexpected exception."));
        }

        await EmitAsync(run, isAgent ? ExecutionEventKind.AgentCompleted : ExecutionEventKind.ToolCompleted, step,
            outcome: inv.Outcome, failure: inv.Failure, artifacts: inv.Artifacts,
            attributes: new Dictionary<string, string>(target) { ["status"] = inv.Status.ToString() });

        switch (inv.Status)
        {
            case InvocationStatus.Cancelled:
                if (ct.IsCancellationRequested)
                {
                    step.Cancel(Now, "Cancellation requested by the host.");
                    await SaveAsync(step);
                    await EmitStepEndAsync(run, step);
                    return StepResult.Cancelled("Cancellation requested by the host.");
                }
                return await FailStepAsync(run, step, timeoutCts.IsCancellationRequested
                    ? new Failure(FailureKind.Timeout, "step_timeout", $"Step '{stepDef.Id}' exceeded its timeout.", Retryable: true)
                    : new Failure(FailureKind.Provider, "unrequested_cancellation", "The agent or tool cancelled itself without being asked."));

            case InvocationStatus.Failed:
                return await FailStepAsync(run, step, inv.Failure
                    ?? new Failure(FailureKind.Internal, "missing_failure", "The agent or tool reported failure without a Failure."));

            case InvocationStatus.NeedsInput:
                if (stepDef.Kind != StepKind.Agent)
                    return await FailStepAsync(run, step, new Failure(FailureKind.Validation, "input_not_supported",
                        "Only agent steps may ask for more input."));
                step.AwaitInput(inv.Output);
                await SaveAsync(step);
                await EmitAsync(run, ExecutionEventKind.StepAwaitingInput, step);
                return StepResult.Waiting(step.Id);

            default:
                var outcome = stepDef.Kind == StepKind.Tool ? RuntimeOptions.ToolCompletedOutcome : inv.Outcome;
                if (CheckOutcome(def, stepDef, outcome) is { } bad) return await FailStepAsync(run, step, bad);
                step.Succeed(Now, outcome!, inv.Output, inv.Artifacts);
                await SaveAsync(step);
                await EmitStepEndAsync(run, step);
                return StepResult.Done(outcome!, inv.Output, inv.Artifacts, step.Id);
        }
    }

    private async ValueTask<Invocation> InvokeAsync(StepDefinition stepDef, StepRun step, StepExecutionContext context,
        JsonElement? answer, CancellationToken ct)
    {
        var declared = stepDef.Capabilities ?? [];
        var input = step.State.Input ?? JsonSerializer.SerializeToElement(new JsonObject());
        if (answer is { } a)
            input = With(input, "answer", a);

        if (stepDef.Kind == StepKind.Agent)
        {
            var id = new AgentId(stepDef.Target!);
            var agent = _agents.Resolve(id);
            if (agent is null)
                return Fail(FailureKind.Validation, "agent_not_found", $"Agent '{id}' is not registered.");
            var denied = agent.Descriptor.Capabilities.FirstOrDefault(c => !declared.Contains(c));
            if (denied is not null)
                return Fail(FailureKind.PolicyDenied, "capability_denied", $"Agent '{id}' needs capability '{denied}', which step '{stepDef.Id}' does not declare.");
            var r = await agent.InvokeAsync(new AgentInvocationRequest(id, context, input), ct);
            return new Invocation(r.Status, r.Outcome, r.Output, r.Failure, r.Artifacts);
        }

        var tool = new ToolName(stepDef.Target!);
        var provider = _tools.Resolve(tool);
        if (provider is null)
            return Fail(FailureKind.Validation, "tool_not_found", $"Tool '{tool}' is not provided.");
        if (!declared.Contains(tool.Value))
            return Fail(FailureKind.PolicyDenied, "capability_denied", $"Step '{stepDef.Id}' does not declare capability '{tool}' needed to call it.");
        var tr = await provider.InvokeAsync(new ToolInvocationRequest(tool, context, input, $"{context.Correlation.RunId}/{step.Id}"), ct);
        return new Invocation(tr.Status, null, tr.Output, tr.Failure, tr.Artifacts);

        static Invocation Fail(FailureKind kind, string code, string message)
            => new(InvocationStatus.Failed, Failure: new Failure(kind, code, message));
    }

    /// <summary>Returns a Validation/PolicyDenied failure if the reported outcome may not drive a transition.</summary>
    private static Failure? CheckOutcome(WorkflowDefinition def, StepDefinition stepDef, string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome))
            return new Failure(FailureKind.Validation, "missing_outcome", $"Step '{stepDef.Id}' reported no outcome.");
        if (stepDef.AllowedOutcomes is { } allowed && !allowed.Contains(outcome))
            return new Failure(FailureKind.Validation, "outcome_not_allowed", $"Outcome '{outcome}' is not allowed for step '{stepDef.Id}'.");
        var terminal = !def.Transitions.Any(t => t.From == stepDef.Id);
        if (!terminal && !def.Transitions.Any(t => t.From == stepDef.Id && t.Outcome == outcome))
            return new Failure(FailureKind.Validation, "no_transition", $"Step '{stepDef.Id}' has no transition for outcome '{outcome}'.");
        return null;
    }

    private async ValueTask<StepResult> FailStepAsync(WorkflowRun run, StepRun step, Failure failure)
    {
        step.Fail(Now, failure);
        await SaveAsync(step);
        await EmitStepEndAsync(run, step);
        return StepResult.Failed(failure);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Emits the terminal event matching the step run's current status.</summary>
    private ValueTask EmitStepEndAsync(WorkflowRun run, StepRun step)
    {
        var s = step.State;
        return s.Status switch
        {
            StepRunStatus.Succeeded => EmitAsync(run, ExecutionEventKind.StepSucceeded, step, outcome: s.Outcome, artifacts: s.Artifacts),
            StepRunStatus.Failed => EmitAsync(run, ExecutionEventKind.StepFailed, step, failure: s.Failure),
            _ => EmitAsync(run, ExecutionEventKind.StepCancelled, step),
        };
    }

    /// <summary>
    /// Builds a redacted event from run (and optional step) state and appends it. Events are recorded after the
    /// state change is saved, and sink failures never affect the run.
    /// </summary>
    private async ValueTask EmitAsync(WorkflowRun run, ExecutionEventKind kind, StepRun? step = null, string? outcome = null,
        Failure? failure = null, string? actor = null, IReadOnlyList<ArtifactRef>? artifacts = null,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        var r = run.State;
        if (kind == ExecutionEventKind.RunCancelled && r.CancellationReason is { } runReason)
            attributes = new Dictionary<string, string> { ["reason"] = runReason };
        else if (kind == ExecutionEventKind.StepCancelled && step?.State.CancellationReason is { } stepReason)
            attributes = new Dictionary<string, string> { ["reason"] = stepReason };
        var e = new ExecutionEvent(_ids.NewId("evt"), 0, Now, kind, ExecutionEventKinds.ChannelOf(kind), r.Id, r.CorrelationId,
            r.WorkflowId, r.WorkflowVersion, step?.State.StepId, step?.Id, step?.State.Attempt,
            outcome, EventRedactor.Redact(failure), actor is null ? null : EventRedactor.Redact(actor),
            artifacts is { Count: > 0 } ? artifacts : null, EventRedactor.Redact(attributes));
        try
        {
            await _events.AppendAsync(e, CancellationToken.None);
        }
        catch (Exception)
        {
            // Observability must not change execution results.
        }
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    // Saves run to completion even if the host token is cancelled, so a transition is never half-applied by cancellation.
    // The store returns the snapshot with its new revision; the live object continues from that.
    private async ValueTask SaveAsync(WorkflowRun run) => run.Persisted(await _store.SaveAsync(run.State, CancellationToken.None));
    private async ValueTask SaveAsync(StepRun step) => step.Persisted(await _store.SaveAsync(step.State, CancellationToken.None));

    private async ValueTask CheckpointAsync(WorkflowRun run, CheckpointKind kind, StepId? step, StepRunId? stepRun, string? outcome = null,
        StepId? next = null, IReadOnlyList<ArtifactRef>? artifacts = null)
        => await _store.AppendCheckpointAsync(new RunCheckpoint(run.Id, 0, kind, Now, step, stepRun, outcome, next, run.State.Revision,
            artifacts is { Count: > 0 } ? artifacts : null), CancellationToken.None);

    private static StepDefinition StepOf(WorkflowDefinition def, StepId id) => def.Steps.First(s => s.Id == id);

    /// <summary>Step input is the run input plus the latest output of every completed step, keyed by step id.</summary>
    private async ValueTask<JsonElement> BuildInputAsync(WorkflowRun run)
    {
        var steps = new JsonObject();
        foreach (var s in await _store.GetStepRunsAsync(run.Id, CancellationToken.None))
            if (s is { Status: StepRunStatus.Succeeded, Output: { } output })
                steps[s.StepId.Value] = JsonNode.Parse(output.GetRawText());
        var node = new JsonObject
        {
            ["input"] = run.State.Input is { } i ? JsonNode.Parse(i.GetRawText()) : null,
            ["steps"] = steps,
        };
        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonElement With(JsonElement obj, string name, JsonElement value)
    {
        var node = JsonNode.Parse(obj.GetRawText())!.AsObject();
        node[name] = JsonNode.Parse(value.GetRawText());
        return JsonSerializer.SerializeToElement(node);
    }

    private sealed record Invocation(InvocationStatus Status, string? Outcome = null, JsonElement? Output = null,
        Failure? Failure = null, IReadOnlyList<ArtifactRef>? Artifacts = null);

    private enum StepResultKind { Done, Waiting, Failed, Cancelled }

    private readonly record struct StepResult(StepResultKind Kind, string? Outcome = null, JsonElement? Output = null,
        IReadOnlyList<ArtifactRef>? Artifacts = null, Failure? Failure = null, string? Reason = null, StepRunId? StepRun = null)
    {
        public static StepResult Done(string outcome, JsonElement? output, IReadOnlyList<ArtifactRef>? artifacts, StepRunId stepRun)
            => new(StepResultKind.Done, outcome, output, artifacts, StepRun: stepRun);
        public static StepResult Waiting(StepRunId stepRun) => new(StepResultKind.Waiting, StepRun: stepRun);
        public static StepResult Failed(Failure failure) => new(StepResultKind.Failed, Failure: failure);
        public static StepResult Cancelled(string reason) => new(StepResultKind.Cancelled, Reason: reason);
    }
}
