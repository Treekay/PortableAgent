namespace PortableAgent.Persistence.Sqlite.Entities;

public sealed class RunStateEntity
{
    public Guid RunId { get; set; }
    public string RuntimeDefinitionId { get; set; } = "";
    public string Lifecycle { get; set; } = "";
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string StateJson { get; set; } = "";
}
