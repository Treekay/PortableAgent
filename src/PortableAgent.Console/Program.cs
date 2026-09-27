using PortableAgent.Core.Execution;
using PortableAgent.Core.Policies;
using PortableAgent.Core.Tools;
using PortableAgent.Console;
using PortableAgent.Infrastructure.Models;
using PortableAgent.Infrastructure.Policies;
using PortableAgent.Infrastructure.Tools.FlightBooking;
using PortableAgent.Persistence.Sqlite;

var databasePath = Path.GetFullPath("portable-agent.db");
Console.WriteLine($"Database path: {databasePath}");
if (args.Length == 0)
{
    Console.WriteLine("Commands: init | pause | approve <runId> <approvalId> | reject <runId> <approvalId>");
    return 0;
}
try
{
    if (args is ["init"])
    {
        await SqliteRunStateStore.InitializeAsync(databasePath);
        Console.WriteLine("Migrations applied.");
        return 0;
    }
    var pause = args is ["pause"];
    var runId = Guid.Empty;
    var approvalId = Guid.Empty;
    if (!pause && !(args.Length == 3 && args[0] is "approve" or "reject"
        && Guid.TryParse(args[1], out runId) && Guid.TryParse(args[2], out approvalId)))
    {
        Console.Error.WriteLine("Usage: init | pause | approve <runId> <approvalId> | reject <runId> <approvalId>");
        return 1;
    }
    if (!File.Exists(databasePath))
    {
        Console.Error.WriteLine("Database not found. Run init from this directory first.");
        return 1;
    }
    // Trusted composition identity, never sourced from a prompt or approval command.
    var policy = new InMemoryPolicyEvaluator(new Dictionary<ToolId, PolicyDecision>
    {
        [new("flight-local", "booking.get")] = new(PolicyOutcome.Allow),
        [new("flight-local", "booking.cancel")] = new(PolicyOutcome.RequireApproval,
            "confirm-cancellation-v1", "Confirm this exact cancellation.")
    });
    var runner = new AgentRunner("flight-demo-v1", new FlightBookingScriptedModelProvider(),
        new FlightBookingToolProvider(), new FlightBookingToolExecutor(), new RunLimits(),
        new ConsoleExecutionEventSink(), policy, new SqliteRunStateStore(databasePath));

    AgentRunResult? result;
    if (pause) result = await runner.RunAsync(new("Cancel my booking."));
    else
    {
        var command = await runner.SubmitApprovalAsync(new(runId, approvalId,
            args[0] == "approve" ? ApprovalChoice.Approve : ApprovalChoice.Reject));
        Console.WriteLine($"Approval command: {command.Status}");
        result = command.RunResult;
        if (result is null) return 1;
    }
    Console.WriteLine($"RunId: {result.RunId}");
    Console.WriteLine($"Status: {result.Status}");
    if (result.PendingApproval is { } approval) Console.WriteLine($"ApprovalId: {approval.ApprovalId}");
    if (result.FinalText is not null) Console.WriteLine(result.FinalText);
    if (result.Error is not null) Console.Error.WriteLine(result.Error);
    return result.Status is RunStatus.Completed or RunStatus.AwaitingApproval ? 0 : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Command failed: {exception.Message}");
    return 1;
}
