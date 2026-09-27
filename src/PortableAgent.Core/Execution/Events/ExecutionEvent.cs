using System.Text.Json;

namespace PortableAgent.Core.Execution.Events;

/// <summary>An observation, not a checkpoint from which execution can be restored.</summary>
public sealed record ExecutionEvent(
    Guid EventId,
    Guid RunId,
    long Sequence,
    DateTimeOffset OccurredAt,
    ExecutionEventType EventType,
    JsonElement Payload);
