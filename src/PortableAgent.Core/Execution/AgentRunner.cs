using System.Text.Json;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Core.Models;
using PortableAgent.Core.Policies;
using PortableAgent.Core.Tools;

namespace PortableAgent.Core.Execution;

public sealed class AgentRunner
{
    private readonly IModelProvider _model;
    private readonly IToolProvider _tools;
    private readonly IToolExecutor _executor;
    private readonly RunLimits _limits;
    private readonly IExecutionEventSink _events;
    private readonly IPolicyEvaluator _policy;
    private readonly IRunStateStore? _store;
    private readonly string _runtimeDefinitionId;

    public AgentRunner(string runtimeDefinitionId, IModelProvider model, IToolProvider tools, IToolExecutor executor, RunLimits limits,
        IExecutionEventSink? eventSink = null, IPolicyEvaluator? policyEvaluator = null, IRunStateStore? runStore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDefinitionId);
        _runtimeDefinitionId = runtimeDefinitionId;
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxModelTurns < 1 || limits.MaxToolCalls < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        (_model, _tools, _executor, _limits) = (model, tools, executor, limits);
        _events = eventSink ?? NullExecutionEventSink.Instance;
        _policy = policyEvaluator ?? new AllowAllPolicyEvaluator();
        _store = runStore;
    }

    public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
    {
        var state = new RunState { RunId = Guid.NewGuid(), RuntimeDefinitionId = _runtimeDefinitionId, Limits = _limits };
        return new RunExecution(this, state, cancellationToken, stored: false).StartAsync(request);
    }

    public async Task<ApprovalSubmissionResult> SubmitApprovalAsync(
        ApprovalCommand command, CancellationToken cancellationToken = default)
    {
        // Before CAS, cancellation leaves the pending approval untouched.
        cancellationToken.ThrowIfCancellationRequested();
        if (_store is null) return new(ApprovalSubmissionStatus.NotFound);
        var state = await _store.GetAsync(command.RunId, cancellationToken);
        if (state is null) return new(ApprovalSubmissionStatus.NotFound);
        // Running records are history only: no automatic recovery after a crash following a successful CAS.
        if (!string.Equals(state.RuntimeDefinitionId, _runtimeDefinitionId, StringComparison.Ordinal)
            || state.Lifecycle != RunLifecycleState.AwaitingApproval
            || state.PendingApproval is not { Status: ApprovalStatus.Pending } pending
            || pending.ApprovalId != command.ApprovalId
            || !Enum.IsDefined(command.Decision))
            return new(ApprovalSubmissionStatus.Conflict);

        var resolved = pending with
        {
            Status = command.Decision == ApprovalChoice.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected
        };
        var expectedVersion = state.Version;
        state.Version++;
        state.Lifecycle = RunLifecycleState.Running;
        state.PendingApproval = null;
        state.ResolvedApprovals.Add(resolved.Snapshot());
        var approvalEvents = new[]
        {
            NewEvent(state, ExecutionEventType.ApprovalResolved,
                new { approvalId = resolved.ApprovalId, callId = resolved.ToolCall.CallId, status = resolved.Status.ToString() }),
            NewEvent(state, ExecutionEventType.RunResumed, new { })
        };
        if (!await _store.TryReplaceAsync(state.RunId, expectedVersion, state, approvalEvents, cancellationToken))
            return new(ApprovalSubmissionStatus.Conflict);

        // Only the CAS winner can enter execution. No cancellation check may undo this transition.
        var result = await new RunExecution(this, state, cancellationToken, stored: true).ResumeAsync(resolved, approvalEvents);
        return new(ApprovalSubmissionStatus.Accepted, result);
    }

    // Invocation-local machinery. RunState contains data only, so the active stack can be released on pause.
    private sealed class RunExecution(AgentRunner owner, RunState state, CancellationToken token, bool stored)
    {
        private readonly SafeExecutionEventSink _sink = new(owner._events, token);
        private bool _stored = stored;

        private ExecutionEvent CreateEvent(ExecutionEventType type, object payload) => NewEvent(state, type, payload);

        private async ValueTask EmitAsync(ExecutionEventType type, object payload)
        {
            var executionEvent = CreateEvent(type, payload);
            if (owner._store is not null)
            {
                try { await owner._store.AppendEventAsync(executionEvent, token); }
                catch (Exception ex) when (type == ExecutionEventType.ToolExecutionCompleted
                    && !(ex is OperationCanceledException && token.IsCancellationRequested))
                {
                    throw new IOException("Tool returned; its business side effect may already have happened. "
                        + "Persisting ToolExecutionCompleted failed; the tool was not retried.", ex);
                }
            }
            await _sink.PublishAsync(executionEvent, token);
        }

        public async Task<AgentRunResult> StartAsync(AgentRunRequest request)
        {
            AgentRunResult result;
            try
            {
                var started = CreateEvent(ExecutionEventType.RunStarted, new { });
                if (owner._store is not null)
                {
                    await owner._store.CreateAsync(state, [started], token);
                    _stored = true;
                }
                await _sink.PublishAsync(started, token);
                token.ThrowIfCancellationRequested();
                await DiscoverAsync();
                state.Conversation.Add(AgentMessage.User(request.UserMessage));
                result = await ContinueAsync();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { result = new(RunStatus.Cancelled); }
            catch (Exception exception) { result = Failed(exception.Message); }
            return await FinishAsync(result);
        }

        public async Task<AgentRunResult> ResumeAsync(PendingApproval resolved, IReadOnlyList<ExecutionEvent> committedEvents)
        {
            AgentRunResult result;
            // These facts follow a successful CAS even if the caller cancels immediately afterwards.
            foreach (var executionEvent in committedEvents)
                await _sink.PublishAsync(executionEvent, CancellationToken.None);
            try
            {
                token.ThrowIfCancellationRequested();
                // Rebuild trusted capabilities even after rejection: the model may propose another tool next.
                await DiscoverAsync();
                if (resolved.Status == ApprovalStatus.Rejected)
                {
                    AppendNonExecuted(resolved.ToolCall, ToolResultDisposition.RejectedByUser, "User rejected this operation.");
                    result = await ContinueAsync();
                }
                else
                {
                    var current = state.ToolCatalog.SingleOrDefault(tool => tool.Id == resolved.ToolDefinition.Id);
                    if (current is null || current.ModelName != resolved.ToolDefinition.ModelName
                        || !JsonElement.DeepEquals(current.InputSchema, resolved.ToolDefinition.InputSchema))
                        result = Failed("Approved tool is missing or its contract has changed.");
                    else
                    {
                        var decision = await EvaluateAsync(current, resolved.ToolCall);
                        if (decision.Outcome == PolicyOutcome.Deny)
                        {
                            AppendNonExecuted(resolved.ToolCall, ToolResultDisposition.DeniedByPolicy,
                                decision.Reason ?? "Current policy denied this operation.");
                            result = await ContinueAsync();
                        }
                        else if (decision.Outcome == PolicyOutcome.RequireApproval
                            && !SamePolicy(resolved, decision))
                            result = await PauseAsync(current, resolved.ToolCall, decision);
                        else
                        {
                            var execution = await ExecuteToolAsync(current, resolved.ToolCall);
                            result = execution.Terminal ?? await ContinueAsync();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { result = new(RunStatus.Cancelled); }
            catch (Exception exception) { result = Failed(exception.Message); }
            return await FinishAsync(result);
        }

        private static bool SamePolicy(PendingApproval approval, PolicyDecision current) =>
            // Named policies use ordinal identity. Anonymous policies match only if both reasons also match.
            string.Equals(approval.PolicyId, current.PolicyId, StringComparison.Ordinal)
            && (current.PolicyId is not null || string.Equals(approval.PolicyReason, current.Reason, StringComparison.Ordinal));

        private async Task DiscoverAsync()
        {
            await EmitAsync(ExecutionEventType.ToolDiscoveryStarted, new { });
            var discovered = await owner._tools.GetToolsAsync(token);
            await EmitAsync(ExecutionEventType.ToolDiscoveryCompleted,
                new { toolCount = discovered.Count, tools = discovered.Select(tool => tool.ModelName).ToArray() });
            token.ThrowIfCancellationRequested();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<ToolId>();
            foreach (var tool in discovered)
            {
                if (string.IsNullOrWhiteSpace(tool.ModelName) || string.IsNullOrWhiteSpace(tool.Id.SourceId)
                    || string.IsNullOrWhiteSpace(tool.Id.Name))
                    throw new InvalidOperationException("Tool identity must not be empty.");
                if (!names.Add(tool.ModelName) || !ids.Add(tool.Id))
                    throw new InvalidOperationException("Tool ModelName and ToolId must both be unique.");
            }
            state.ToolCatalog = discovered.Select(tool => tool with { InputSchema = tool.InputSchema.Clone() }).ToList();
        }

        private async Task<PolicyDecision> EvaluateAsync(ToolDefinition tool, ToolCall call)
        {
            await EmitAsync(ExecutionEventType.PolicyEvaluationStarted, new { callId = call.CallId });
            var decision = await owner._policy.EvaluateAsync(new(state.RunId, tool, call), token);
            if (!Enum.IsDefined(decision.Outcome)) throw new InvalidOperationException("Invalid policy outcome.");
            await EmitAsync(ExecutionEventType.PolicyEvaluationCompleted,
                new { callId = call.CallId, outcome = decision.Outcome.ToString(), policyId = decision.PolicyId });
            token.ThrowIfCancellationRequested();
            return decision;
        }

        private async Task<AgentRunResult> ContinueAsync()
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (state.ModelTurns >= state.Limits.MaxModelTurns) return Limited("Maximum model turns reached.");
                var request = new ModelRequest(Array.AsReadOnly(state.Conversation.ToArray()),
                    Array.AsReadOnly(state.ToolCatalog.ToArray()));
                state.ModelTurns++;
                await EmitAsync(ExecutionEventType.ModelTurnStarted, new { turn = state.ModelTurns });
                var reply = await owner._model.GenerateAsync(request, token);
                await EmitAsync(ExecutionEventType.ModelTurnCompleted,
                    new { turn = state.ModelTurns, finishReason = reply.FinishReason.ToString(), toolCallCount = reply.ToolCalls.Count });
                foreach (var call in reply.ToolCalls)
                    await EmitAsync(ExecutionEventType.ToolCallProposed, new { callId = call.CallId, toolName = call.ToolName });
                token.ThrowIfCancellationRequested();
                if (reply.FinishReason == ModelFinishReason.Completed)
                {
                    if (reply.ToolCalls.Count != 0 || string.IsNullOrWhiteSpace(reply.Text))
                        return Failed("A completed reply must contain final text and no tool calls.");
                    state.Conversation.Add(AgentMessage.AssistantFinal(reply.Text));
                    return new(RunStatus.Completed, reply.Text);
                }
                if (reply.FinishReason != ModelFinishReason.ToolCalls || reply.ToolCalls.Count == 0)
                    return Failed("A tool reply must contain tool calls.");

                var proposal = AgentMessage.AssistantToolRequest(reply.Text, reply.ToolCalls);
                state.Conversation.Add(proposal);
                var registry = state.ToolCatalog.ToDictionary(tool => tool.ModelName, StringComparer.Ordinal);
                foreach (var call in proposal.ToolCalls)
                {
                    if (string.IsNullOrWhiteSpace(call.CallId) || !state.UsedCallIds.Add(call.CallId))
                        return Failed("Tool CallId must be non-empty and unique within a Run.");
                    if (!registry.ContainsKey(call.ToolName)) return Failed($"Unknown tool: {call.ToolName}.");
                }
                if (proposal.ToolCalls.Count > state.Limits.MaxToolCalls - state.ToolCalls)
                    return Limited("Maximum tool calls would be exceeded.");

                var decisions = new List<PolicyDecision>();
                foreach (var call in proposal.ToolCalls)
                    decisions.Add(await EvaluateAsync(registry[call.ToolName], call));

                // No operation in this batch is attempted until every policy has been evaluated.
                if (decisions.Any(decision => decision.Outcome == PolicyOutcome.Deny))
                {
                    for (var i = 0; i < proposal.ToolCalls.Count; i++)
                    {
                        var denied = decisions[i].Outcome == PolicyOutcome.Deny;
                        AppendNonExecuted(proposal.ToolCalls[i], denied ? ToolResultDisposition.DeniedByPolicy
                            : ToolResultDisposition.NotExecutedDueToBatchPolicy,
                            denied ? decisions[i].Reason ?? "Policy denied this operation." : "Another call in this batch was denied.");
                    }
                    continue;
                }
                if (decisions.Any(decision => decision.Outcome == PolicyOutcome.RequireApproval))
                {
                    if (proposal.ToolCalls.Count != 1) return Failed("Multi-call approval is not supported in Phase 4A.");
                    return await PauseAsync(registry[proposal.ToolCalls[0].ToolName], proposal.ToolCalls[0], decisions[0]);
                }
                for (var i = 0; i < proposal.ToolCalls.Count; i++)
                {
                    var call = proposal.ToolCalls[i];
                    var execution = await ExecuteToolAsync(registry[call.ToolName], call);
                    if (execution.Terminal is { } terminal) return terminal;
                    if (execution.BusinessFailed)
                    {
                        for (var j = i + 1; j < proposal.ToolCalls.Count; j++)
                            AppendNonExecuted(proposal.ToolCalls[j], ToolResultDisposition.NotExecutedDueToPriorFailure,
                                "An earlier tool call in this batch failed; this call was not executed.");
                        break;
                    }
                }
            }
        }

        private void AppendNonExecuted(ToolCall call, ToolResultDisposition disposition, string reason) =>
            state.Conversation.Add(AgentMessage.FromToolResult(new(call.CallId, false, Error: reason, Disposition: disposition)));

        private async Task<(AgentRunResult? Terminal, bool BusinessFailed)> ExecuteToolAsync(ToolDefinition tool, ToolCall call)
        {
            token.ThrowIfCancellationRequested();
            if (state.ToolCalls >= state.Limits.MaxToolCalls) return (Limited("Maximum tool calls reached."), false);
            await EmitAsync(ExecutionEventType.ToolExecutionStarted,
                new { callId = call.CallId, toolId = $"{tool.Id.SourceId}/{tool.Id.Name}", toolName = call.ToolName });
            state.ToolCalls++; // Count invocation, not a failed attempt to persist its start event.
            var result = await owner._executor.ExecuteAsync(tool, call, token);
            if (result.CallId != call.CallId) return (Failed("Tool result CallId does not match the requested call."), false);
            if (result.Disposition != ToolResultDisposition.Executed)
                return (Failed("An invoked executor must return an Executed disposition."), false);
            // Completed means the returned result passed Runtime correlation and disposition checks.
            await EmitAsync(ExecutionEventType.ToolExecutionCompleted,
                new { callId = call.CallId, toolName = call.ToolName, success = result.IsSuccess });
            token.ThrowIfCancellationRequested();
            state.Conversation.Add(AgentMessage.FromToolResult(result));
            return (null, !result.IsSuccess);
        }

        private async Task<AgentRunResult> PauseAsync(ToolDefinition tool, ToolCall call, PolicyDecision decision)
        {
            if (owner._store is null) return Failed("An approval policy requires a Run state store.");
            state.PendingApproval = new PendingApproval(Guid.NewGuid(), state.RunId, tool, call,
                decision.PolicyId, decision.Reason, DateTimeOffset.UtcNow, ApprovalStatus.Pending).Snapshot();
            state.Lifecycle = RunLifecycleState.AwaitingApproval;
            var approvalEvent = CreateEvent(ExecutionEventType.ApprovalRequired,
                new { approvalId = state.PendingApproval.ApprovalId, callId = call.CallId, policyId = decision.PolicyId });
            // Snapshot includes the reserved sequence. A failed save must never report AwaitingApproval.
            await ReplaceStoredAsync([approvalEvent], token);
            await _sink.PublishAsync(approvalEvent, token);
            return new(RunStatus.AwaitingApproval) { RunId = state.RunId, PendingApproval = state.PendingApproval.Snapshot() };
        }

        private async Task ReplaceStoredAsync(IReadOnlyList<ExecutionEvent> events, CancellationToken cancellationToken)
        {
            var previous = state.Version;
            var replacement = state.Snapshot();
            replacement.Version = previous + 1;
            if (!await owner._store!.TryReplaceAsync(state.RunId, previous, replacement, events, cancellationToken))
                throw new InvalidOperationException("Stored Run version conflict.");
            state.Version = replacement.Version;
        }

        private async Task<AgentRunResult> FinishAsync(AgentRunResult result)
        {
            if (result.Status == RunStatus.AwaitingApproval) return result;
            result = result with { RunId = state.RunId };
            state.PendingApproval = null;
            state.Lifecycle = result.Status switch
            {
                RunStatus.Completed => RunLifecycleState.Completed,
                RunStatus.Cancelled => RunLifecycleState.Cancelled,
                RunStatus.LimitReached => RunLifecycleState.LimitReached,
                _ => RunLifecycleState.Failed
            };
            var terminal = CreateEvent(TerminalType(result.Status),
                new { status = result.Status.ToString(), modelTurns = state.ModelTurns, toolCalls = state.ToolCalls });
            if (_stored)
            {
                try { await ReplaceStoredAsync([terminal], CancellationToken.None); }
                catch (Exception exception)
                {
                    // No live event without a durable event. Database may still show Running.
                    return Failed($"Final persistence failed. {result.Error} {exception.Message}") with { RunId = state.RunId };
                }
            }
            if (owner._store is null || _stored)
                await _sink.PublishAsync(terminal, CancellationToken.None);
            return result;
        }
    }

    private static ExecutionEvent NewEvent(RunState state, ExecutionEventType type, object payload) =>
        new(Guid.NewGuid(), state.RunId, ++state.LastSequence, DateTimeOffset.UtcNow,
            type, JsonSerializer.SerializeToElement(payload));

    private static ExecutionEventType TerminalType(RunStatus status) => status switch
    {
        RunStatus.Completed => ExecutionEventType.RunCompleted,
        RunStatus.Cancelled => ExecutionEventType.RunCancelled,
        RunStatus.LimitReached => ExecutionEventType.RunLimitReached,
        _ => ExecutionEventType.RunFailed
    };
    private static AgentRunResult Failed(string error) => new(RunStatus.Failed, Error: error);
    private static AgentRunResult Limited(string error) => new(RunStatus.LimitReached, Error: error);
}
