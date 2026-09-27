namespace PortableAgent.Persistence.Sqlite.Entities;

public sealed class ExecutionEventEntity
{
    public Guid EventId { get; set; }
    public Guid RunId { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}
