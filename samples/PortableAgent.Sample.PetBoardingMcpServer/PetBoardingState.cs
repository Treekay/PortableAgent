namespace PortableAgent.Sample.PetBoardingMcpServer;

/// <summary>Business state belongs to this server process, independently of MCP sessions.</summary>
public sealed class PetBoardingState
{
    private readonly object _gate = new();
    private readonly List<StaffTask> _tasks = [];

    public IReadOnlyList<StaffTask> Tasks { get { lock (_gate) return _tasks.ToArray(); } }

    public CareRecords GetCareRecords(string petName)
    {
        ValidatePet(petName);
        return new(petName, [new("feeding", "08:00", "completed")]);
    }

    public StaffTask CreateStaffTask(string petName, string task)
    {
        ValidatePet(petName);
        if (string.IsNullOrWhiteSpace(task)) throw new ArgumentException("Task must not be empty.");
        lock (_gate)
        {
            var created = new StaffTask($"task-{_tasks.Count + 1}", petName, task, "created");
            _tasks.Add(created);
            return created;
        }
    }

    private static void ValidatePet(string name)
    {
        if (name != "Cooper") throw new ArgumentException("Unknown pet.");
    }
}

public sealed record CareRecord(string Type, string Time, string Status);
public sealed record CareRecords(string PetName, IReadOnlyList<CareRecord> Records);
public sealed record StaffTask(string TaskId, string PetName, string Task, string Status);
