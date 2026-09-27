using System.Text.Json;
using PortableAgent.Core.Tools;

namespace PortableAgent.Infrastructure.Tools.FlightBooking;

/// <summary>Stateless local simulation. No external services or persistent side effects.</summary>
public sealed class FlightBookingToolExecutor : IToolExecutor
{
    public Task<ToolResult> ExecuteAsync(
        ToolDefinition registeredTool, ToolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Dispatch only by the trusted definition resolved by AgentRunner.
        var isRead = registeredTool.Id == FlightBookingToolProvider.ReadId;
        if (!isRead && registeredTool.Id != FlightBookingToolProvider.ActionId)
            return Task.FromResult(new ToolResult(call.CallId, false, Error: "Unknown FlightBooking tool identity."));

        string[] fields = isRead ? ["bookingId"] : ["bookingId"];
        var args = call.Arguments;
        if (args.ValueKind != JsonValueKind.Object
            || args.EnumerateObject().Count() != fields.Length
            || fields.Any(field => !args.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            || (fields.Contains("bookingId") && args.GetProperty("bookingId").GetString() != "NZ123"))
            return Task.FromResult(new ToolResult(call.CallId, false, Error: "Unsupported demo arguments."));

        var json = isRead
            ? """{"bookingId":"NZ123","route":"Auckland → Sydney","date":"2026-10-10","status":"confirmed"}"""
            : """{"bookingId":"NZ123","status":"cancelled"}""";
        using var document = JsonDocument.Parse(json);
        return Task.FromResult(new ToolResult(call.CallId, true, document.RootElement.Clone()));
    }
}
