namespace PortableAgent.Sample.FlightBookingMcpServer;

/// <summary>In-memory business state. Repeated cancellation is a business no-op, not an execution guarantee.</summary>
public sealed class FlightBookingState
{
    private readonly object _gate = new();
    private Booking _booking = new("NZ123", "Auckland → Sydney", "2026-10-10", "confirmed");
    private int _cancelCallCount;
    private int _cancelMutationCount;

    public int CancelCallCount { get { lock (_gate) return _cancelCallCount; } }
    public int CancelMutationCount { get { lock (_gate) return _cancelMutationCount; } }

    public Booking GetBooking(string bookingId)
    {
        ValidateBooking(bookingId);
        lock (_gate) return _booking;
    }

    public Cancellation CancelBooking(string bookingId)
    {
        lock (_gate)
        {
            _cancelCallCount++;
            ValidateBooking(bookingId);
            if (_booking.Status == "confirmed")
            {
                _booking = _booking with { Status = "cancelled" };
                _cancelMutationCount++;
            }
            return new(_booking.BookingId, _booking.Status);
        }
    }

    private static void ValidateBooking(string id)
    {
        if (id != "NZ123") throw new ArgumentException("Unknown booking.");
    }
}

public sealed record Booking(string BookingId, string Route, string Date, string Status);
public sealed record Cancellation(string BookingId, string Status);
