using PortableAgent.Adapters.Mcp;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Models;
using PortableAgent.Core.Policies;
using PortableAgent.Core.Tools;
using PortableAgent.Infrastructure.Models;
using PortableAgent.Infrastructure.Policies;
using PortableAgent.Persistence.Sqlite;

namespace PortableAgent.Console;

internal static class McpDomainDemo
{
    public const string Usage = "mcp-pet-read | mcp-pet-denied-task | mcp-flight-read | mcp-flight-pause | mcp-flight-approve <runId> <approvalId> | mcp-flight-reject <runId> <approvalId>";

    public static bool IsCommand(string command) => command is "mcp-pet-read" or "mcp-pet-denied-task"
        or "mcp-flight-read" or "mcp-flight-pause" or "mcp-flight-approve" or "mcp-flight-reject";

    public static async Task<AgentRunResult?> RunAsync(string[] args, string databasePath)
    {
        var resume = args[0] is "mcp-flight-approve" or "mcp-flight-reject";
        var runId = Guid.Empty;
        var approvalId = Guid.Empty;
        if (resume ? args.Length != 3 || !Guid.TryParse(args[1], out runId) || !Guid.TryParse(args[2], out approvalId)
            : args.Length != 1)
        {
            System.Console.Error.WriteLine(Usage);
            return null;
        }
        var pet = args[0] is "mcp-pet-read" or "mcp-pet-denied-task";
        // Host-controlled trusted compositions. The URL is not an automatically derived identity.
        var sourceId = pet ? "pet-mcp" : "flight-mcp";
        var definitionId = pet ? "pet-mcp-demo-v1" : "flight-mcp-demo-v1";
        var endpoint = new Uri(pet ? "http://localhost:5102/mcp" : "http://localhost:5103/mcp");
        IModelProvider model = pet ? new PetBoardingScriptedModelProvider() : new FlightBookingScriptedModelProvider();
        var policy = new InMemoryPolicyEvaluator(pet
            ? new Dictionary<ToolId, PolicyDecision>
            {
                [new(sourceId, "get_care_records")] = new(PolicyOutcome.Allow),
                [new(sourceId, "create_staff_task")] = new(PolicyOutcome.Deny, Reason: "Staff task creation is disabled in this demo.")
            }
            : new Dictionary<ToolId, PolicyDecision>
            {
                [new(sourceId, "get_booking")] = new(PolicyOutcome.Allow),
                [new(sourceId, "cancel_booking")] = new(PolicyOutcome.RequireApproval,
                    "confirm-flight-cancellation-v1", "Confirm this exact remote cancellation.")
            });
        await using var adapter = new McpToolAdapter(new(sourceId, endpoint));
        var runner = new AgentRunner(definitionId, model, adapter, adapter, new(),
            new ConsoleExecutionEventSink(), policy, new SqliteRunStateStore(databasePath));
        if (resume)
        {
            var result = await runner.SubmitApprovalAsync(new(runId, approvalId,
                args[0] == "mcp-flight-approve" ? ApprovalChoice.Approve : ApprovalChoice.Reject));
            System.Console.WriteLine($"Approval command: {result.Status}");
            return result.RunResult;
        }
        var prompt = args[0] switch
        {
            "mcp-pet-read" => "Has Cooper eaten today?",
            "mcp-pet-denied-task" => "Ask the staff to give Cooper some fresh water.",
            "mcp-flight-read" => "Show my booking.",
            _ => "Cancel my booking."
        };
        return await runner.RunAsync(new(prompt));
    }
}
