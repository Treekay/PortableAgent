using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;

namespace PortableAgent.Core.Execution;

public sealed class AgentRunner
{
    private readonly IModelProvider _model;
    private readonly IToolProvider _tools;
    private readonly IToolExecutor _executor;
    private readonly RunLimits _limits;

    public AgentRunner(IModelProvider model, IToolProvider tools, IToolExecutor executor, RunLimits limits)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxModelTurns < 1 || limits.MaxToolCalls < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));

        (_model, _tools, _executor, _limits) = (model, tools, executor, limits);
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var discovered = await _tools.GetToolsAsync(cancellationToken);
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
            var modelTurns = 0;
            var toolCalls = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (modelTurns >= _limits.MaxModelTurns)
                    return Limited("Maximum model turns reached.");

                // Each model request retains its own history snapshot, never the mutable list.
                var modelRequest = new ModelRequest(Array.AsReadOnly(conversation.ToArray()), availableTools);
                modelTurns++;
                var reply = await _model.GenerateAsync(modelRequest, cancellationToken);
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
                    var result = await _executor.ExecuteAsync(registry[call.ToolName], call, cancellationToken);
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

    private static AgentRunResult Failed(string error) => new(RunStatus.Failed, Error: error);
    private static AgentRunResult Limited(string error) => new(RunStatus.LimitReached, Error: error);
}
