using System.Text.Json;
using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;

namespace PortableAgent.Infrastructure.Models;

/// <summary>Recognizes only two fixed demo inputs, then validates structured tool results.</summary>
public sealed class PetBoardingScriptedModelProvider : IModelProvider
{
    public Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Messages.Count == 0 || request.Messages[0].Role != MessageRole.User)
            throw new InvalidOperationException("Expected a user message.");

        var isRead = request.Messages[0].Text switch
        {
            "Has Cooper eaten today?" => true,
            "Ask the staff to give Cooper some fresh water." => false,
            _ => throw new InvalidOperationException("Unsupported PetBoarding demo input.")
        };
        var toolName = isRead ? "get_care_records" : "create_staff_task";
        if (!request.Tools.Any(tool => tool.ModelName == toolName))
            throw new InvalidOperationException("Required demo tool is not registered.");

        var arguments = isRead
            ? JsonSerializer.SerializeToElement(new { petName = "Cooper" })
            : JsonSerializer.SerializeToElement(new { petName = "Cooper", task = "Give fresh water" });

        if (request.Messages.Count == 1)
            return Task.FromResult(new ModelReply(null,
                [new ToolCall("call-1", toolName, arguments)], ModelFinishReason.ToolCalls));

        if (request.Messages.Count != 3
            || request.Messages[1].Role != MessageRole.Assistant
            || request.Messages[1].ToolCalls.Count != 1
            || request.Messages[1].ToolCalls[0].CallId != "call-1"
            || request.Messages[1].ToolCalls[0].ToolName != toolName
            || !JsonElement.DeepEquals(request.Messages[1].ToolCalls[0].Arguments, arguments)
            || request.Messages[2].Role != MessageRole.Tool
            || request.Messages[2].ToolResult is not { CallId: "call-1", IsSuccess: true, Output: { } output })
            throw new InvalidOperationException("Conversation does not match the PetBoarding script.");

        // Validate business fields before returning any success confirmation.
        var valid = isRead
            ? output.GetProperty("petName").GetString() == "Cooper"
                && output.GetProperty("records").GetArrayLength() == 1
                && output.GetProperty("records")[0].GetProperty("type").GetString() == "feeding"
                && output.GetProperty("records")[0].GetProperty("time").GetString() == "08:00"
                && output.GetProperty("records")[0].GetProperty("status").GetString() == "completed"
            : output.GetProperty("taskId").GetString() == "task-1"
                && output.GetProperty("petName").GetString() == "Cooper"
                && output.GetProperty("task").GetString() == "Give fresh water"
                && output.GetProperty("status").GetString() == "created";
        if (!valid)
            throw new InvalidOperationException("Unexpected PetBoarding tool result.");

        return Task.FromResult(new ModelReply(isRead
            ? "Yes. Cooper was fed at 08:00."
            : "Task task-1 was created: Give fresh water to Cooper.", [], ModelFinishReason.Completed));
    }
}
