using System.Text.Json;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;

namespace PortableAgent.Core.Execution;

public sealed class AgentRunner
{
    private readonly IModelProvider _model;
    private readonly IToolProvider _tools;
    private readonly IToolExecutor _executor;
    private readonly RunLimits _limits;
    private readonly IExecutionEventSink _events;

    public AgentRunner(IModelProvider model, IToolProvider tools, IToolExecutor executor, RunLimits limits,
        IExecutionEventSink? eventSink = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxModelTurns < 1 || limits.MaxToolCalls < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));

        (_model, _tools, _executor, _limits) = (model, tools, executor, limits);
        _events = eventSink ?? NullExecutionEventSink.Instance;
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request, CancellationToken cancellationToken = default)
    {
        // Identity and ordering belong to this invocation, never to the user message or shared Runner.
        var runId = Guid.NewGuid();
        long sequence = 0;
        var modelTurns = 0;
        var toolCalls = 0;
        var safeSink = new SafeExecutionEventSink(_events, cancellationToken);

        ValueTask EmitAsync(ExecutionEventType type, object payload, bool terminal = false)
        {
            var executionEvent = new ExecutionEvent(Guid.NewGuid(), runId, ++sequence,
                DateTimeOffset.UtcNow, type, JsonSerializer.SerializeToElement(payload));
            // A cancelled Run still has a terminal fact to report. Delivery failures can leave sequence gaps.
            return safeSink.PublishAsync(executionEvent, terminal ? CancellationToken.None : cancellationToken);
        }

        await EmitAsync(ExecutionEventType.RunStarted, new { });
        var result = await ExecuteRunAsync();
        result = result with { RunId = runId };
        var terminalType = result.Status switch
        {
            RunStatus.Completed => ExecutionEventType.RunCompleted,
            RunStatus.Cancelled => ExecutionEventType.RunCancelled,
            RunStatus.LimitReached => ExecutionEventType.RunLimitReached,
            _ => ExecutionEventType.RunFailed
        };
        // Do not copy final answers, exception messages, tool arguments or outputs into trace payloads.
        await EmitAsync(terminalType, new { status = result.Status.ToString(), modelTurns, toolCalls }, terminal: true);
        return result;

        // Keep early execution exits separate from the single terminal-event publication above.
        async Task<AgentRunResult> ExecuteRunAsync()
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EmitAsync(ExecutionEventType.ToolDiscoveryStarted, new { });
                var discovered = await _tools.GetToolsAsync(cancellationToken);
                await EmitAsync(ExecutionEventType.ToolDiscoveryCompleted,
                    new { toolCount = discovered.Count, tools = discovered.Select(tool => tool.ModelName).ToArray() });
                cancellationToken.ThrowIfCancellationRequested();
                var registry = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);
                var toolIds = new HashSet<ToolId>();
                foreach (var definition in discovered)
                {
                    if (string.IsNullOrWhiteSpace(definition.ModelName)
                        || string.IsNullOrWhiteSpace(definition.Id.SourceId)
                        || string.IsNullOrWhiteSpace(definition.Id.Name))
                        return Failed("Tool identity must not be empty.");

                    var ownedDefinition = definition with { InputSchema = definition.InputSchema.Clone() };
                    if (!registry.TryAdd(definition.ModelName, ownedDefinition) || !toolIds.Add(definition.Id))
                        return Failed("Tool ModelName and ToolId must both be unique.");
                }

                var availableTools = Array.AsReadOnly(registry.Values.ToArray());
                var conversation = new List<AgentMessage> { AgentMessage.User(request.UserMessage) };
                var callIds = new HashSet<string>(StringComparer.Ordinal);

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (modelTurns >= _limits.MaxModelTurns)
                        return Limited("Maximum model turns reached.");

                    // Each model request retains its own history snapshot, never the mutable list.
                    var modelRequest = new ModelRequest(Array.AsReadOnly(conversation.ToArray()), availableTools);
                    modelTurns++;
                    await EmitAsync(ExecutionEventType.ModelTurnStarted, new { turn = modelTurns });
                    var reply = await _model.GenerateAsync(modelRequest, cancellationToken);
                    await EmitAsync(ExecutionEventType.ModelTurnCompleted,
                        new { turn = modelTurns, finishReason = reply.FinishReason.ToString(), toolCallCount = reply.ToolCalls.Count });
                    // Proposals are facts even when the reply or tool will subsequently fail validation.
                    foreach (var call in reply.ToolCalls)
                        await EmitAsync(ExecutionEventType.ToolCallProposed,
                            new { callId = call.CallId, toolName = call.ToolName });
                    cancellationToken.ThrowIfCancellationRequested();

                    if (reply.FinishReason == ModelFinishReason.Completed)
                    {
                        if (reply.ToolCalls.Count != 0 || string.IsNullOrWhiteSpace(reply.Text))
                            return Failed("A completed reply must contain final text and no tool calls.");

                        conversation.Add(AgentMessage.AssistantFinal(reply.Text));
                        return new(RunStatus.Completed, reply.Text);
                    }

                    if (reply.FinishReason != ModelFinishReason.ToolCalls || reply.ToolCalls.Count == 0)
                        return Failed("A tool reply must contain tool calls.");

                    var assistantMessage = AgentMessage.AssistantToolRequest(reply.Text, reply.ToolCalls);
                    conversation.Add(assistantMessage);

                    // Validate the complete proposal before any tool in the batch can execute.
                    foreach (var call in assistantMessage.ToolCalls)
                    {
                        if (string.IsNullOrWhiteSpace(call.CallId) || !callIds.Add(call.CallId))
                            return Failed("Tool CallId must be non-empty and unique within a Run.");
                        if (!registry.ContainsKey(call.ToolName))
                            return Failed($"Unknown tool: {call.ToolName}.");
                    }

                    if (assistantMessage.ToolCalls.Count > _limits.MaxToolCalls - toolCalls)
                        return Limited("Maximum tool calls would be exceeded.");

                    foreach (var call in assistantMessage.ToolCalls)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        toolCalls++;
                        var toolId = registry[call.ToolName].Id;
                        await EmitAsync(ExecutionEventType.ToolExecutionStarted,
                            new { callId = call.CallId, toolId = $"{toolId.SourceId}/{toolId.Name}", toolName = call.ToolName });
                        var result = await _executor.ExecuteAsync(registry[call.ToolName], call, cancellationToken);
                        await EmitAsync(ExecutionEventType.ToolExecutionCompleted,
                            new { callId = call.CallId, toolName = call.ToolName, success = result.IsSuccess });
                        cancellationToken.ThrowIfCancellationRequested();
                        if (result.CallId != call.CallId)
                            return Failed("Tool result CallId does not match the requested call.");
                        if (!result.IsSuccess)
                            return Failed(result.Error ?? "Tool execution failed.");

                        conversation.Add(AgentMessage.FromToolResult(result));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(RunStatus.Cancelled);
            }
            catch (Exception exception)
            {
                // This small local host reports failures without retrying operations.
                return Failed(exception.Message);
            }
        }
    }

    private static AgentRunResult Failed(string error) => new(RunStatus.Failed, Error: error);
    private static AgentRunResult Limited(string error) => new(RunStatus.LimitReached, Error: error);
}
