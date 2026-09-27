using System.Text.Json;
using PortableAgent.Core.Execution.Events;

namespace PortableAgent.Api.Streaming;

public sealed record SseEventDto(Guid EventId, Guid RunId, long Sequence, DateTimeOffset OccurredAt,
    string EventType, JsonElement Payload)
{
    public static SseEventDto FromEvent(ExecutionEvent item) =>
        new(item.EventId, item.RunId, item.Sequence, item.OccurredAt, item.EventType.ToString(), item.Payload.Clone());
}
