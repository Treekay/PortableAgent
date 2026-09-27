using System.Text.Json;
using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;

namespace PortableAgent.Infrastructure.Models;

/// <summary>Recognizes only two fixed demo inputs, then validates structured tool results.</summary>
public sealed class FlightBookingScriptedModelProvider : IModelProvider
{
    public Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Messages.Count == 0 || request.Messages[0].Role != MessageRole.User)
            throw new InvalidOperationException("Expected a user message.");

        var isRead = request.Messages[0].Text switch
        {
            "Show my booking." => true,
            "Cancel my booking." => false,
            _ => throw new InvalidOperationException("Unsupported FlightBooking demo input.")
        };
        var toolName = isRead ? "get_booking" : "cancel_booking";
        if (!request.Tools.Any(tool => tool.ModelName == toolName))
            throw new InvalidOperationException("Required demo tool is not registered.");

        var arguments = isRead
            ? JsonSerializer.SerializeToElement(new { bookingId = "NZ123" })
            : JsonSerializer.SerializeToElement(new { bookingId = "NZ123" });

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
            throw new InvalidOperationException("Conversation does not match the FlightBooking script.");

        // Validate business fields before returning any success confirmation.
        var valid = isRead
            ? output.GetProperty("bookingId").GetString() == "NZ123"
                && output.GetProperty("route").GetString() == "Auckland → Sydney"
                && output.GetProperty("date").GetString() == "2026-10-10"
                && output.GetProperty("status").GetString() == "confirmed"
            : output.GetProperty("bookingId").GetString() == "NZ123"
                && output.GetProperty("status").GetString() == "cancelled";
        if (!valid)
            throw new InvalidOperationException("Unexpected FlightBooking tool result.");

        return Task.FromResult(new ModelReply(isRead
            ? "Booking NZ123 is confirmed from Auckland to Sydney on 2026-10-10."
            : "Booking NZ123 has been cancelled.", [], ModelFinishReason.Completed));
    }
}
