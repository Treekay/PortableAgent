using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace PortableAgent.Sample.FlightBookingMcpServer;

public sealed class FlightBookingTools(FlightBookingState state)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ListToolsResult List()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"bookingId":{"type":"string"}},"required":["bookingId"],"additionalProperties":false}""");
        return new() { Tools =
        [
            new() { Name = "get_booking", Description = "Read a booking's current state.", InputSchema = schema.RootElement.Clone() },
            new() { Name = "cancel_booking", Description = "Cancel a booking; an already-cancelled booking stays cancelled.", InputSchema = schema.RootElement.Clone() }
        ] };
    }

    public CallToolResult Call(CallToolRequestParams request)
    {
        try
        {
            if (request.Name is not ("get_booking" or "cancel_booking")) throw new ArgumentException("Unknown Flight Booking tool.");
            if (request.Arguments is not { Count: 1 } args
                || !args.TryGetValue("bookingId", out var id) || id.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Expected exactly one string bookingId argument.");
            object result = request.Name == "get_booking" ? state.GetBooking(id.GetString()!) : state.CancelBooking(id.GetString()!);
            var output = JsonSerializer.SerializeToElement(result, Json);
            return new() { StructuredContent = output, Content = [new TextContentBlock { Text = output.GetRawText() }] };
        }
        catch (ArgumentException ex)
        {
            return new() { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
        }
    }
}
