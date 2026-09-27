using System.Text.Json;
using PortableAgent.Core.Tools;

namespace PortableAgent.Infrastructure.Tools.FlightBooking;

public sealed class FlightBookingToolProvider : IToolProvider
{
    internal static readonly ToolId ReadId = new("flight-local", "booking.get");
    internal static readonly ToolId ActionId = new("flight-local", "booking.cancel");

    public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var readSchema = JsonDocument.Parse("""{"type":"object","properties":{"bookingId":{"type":"string"}},"required":["bookingId"],"additionalProperties":false}""");
        using var actionSchema = JsonDocument.Parse("""{"type":"object","properties":{"bookingId":{"type":"string"}},"required":["bookingId"],"additionalProperties":false}""");
        // Returned elements outlive these temporary documents.
        IReadOnlyList<ToolDefinition> tools =
        [
            new(ReadId, "get_booking", "Returns fixed FlightBooking demo data.", readSchema.RootElement.Clone()),
            new(ActionId, "cancel_booking", "Simulates booking.cancel; no state is persisted.", actionSchema.RootElement.Clone())
        ];
        return Task.FromResult(tools);
    }
}
