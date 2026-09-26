using System.Text.Json;
using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;

namespace PortableAgent.Infrastructure.Models;

/// <summary>A deterministic demo script, not a prompt interpreter or reasoning model.</summary>
public sealed class ScriptedModelProvider : IModelProvider
{
    public Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Tools.Any(tool => tool.ModelName == "calculator_add"))
            throw new InvalidOperationException("The script requires calculator_add.");

        if (request.Messages.Count == 1 && request.Messages[0].Role == MessageRole.User)
        {
            var call = new ToolCall("call-1", "calculator_add",
                JsonSerializer.SerializeToElement(new { a = 2, b = 3 }));
            return Task.FromResult(new ModelReply(null, [call], ModelFinishReason.ToolCalls));
        }

        if (request.Messages.Count == 3
            && request.Messages[0].Role == MessageRole.User
            && request.Messages[1].Role == MessageRole.Assistant
            && request.Messages[1].ToolCalls.Count == 1
            && request.Messages[1].ToolCalls[0] is { CallId: "call-1", ToolName: "calculator_add" }
            && request.Messages[2].Role == MessageRole.Tool
            && request.Messages[2].ToolResult is { CallId: "call-1", IsSuccess: true, Output: { } output }
            && output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Number
            && result.TryGetDecimal(out var value) && value == 5)
            return Task.FromResult(new ModelReply("The result is 5.", [], ModelFinishReason.Completed));

        throw new InvalidOperationException("Conversation does not match the two-turn calculator script.");
    }
}
