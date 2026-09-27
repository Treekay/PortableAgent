using PortableAgent.Core.Execution;
using PortableAgent.Core.Policies;
using PortableAgent.Core.Tools;
using PortableAgent.Console;
using PortableAgent.Infrastructure.Execution;
using PortableAgent.Infrastructure.Models;
using PortableAgent.Infrastructure.Policies;
using PortableAgent.Infrastructure.Tools.FlightBooking;

var policies = new InMemoryPolicyEvaluator(new Dictionary<ToolId, PolicyDecision>
{
    [new("flight-local", "booking.get")] = new(PolicyOutcome.Allow),
    [new("flight-local", "booking.cancel")] = new(PolicyOutcome.RequireApproval, "confirm-cancellation-v1", "Confirm this exact cancellation."),
    [new("pet-local", "care.get_records")] = new(PolicyOutcome.Allow),
    [new("pet-local", "staff.create_task")] = new(PolicyOutcome.Deny, "staff-task-denied")
});
var runner = new AgentRunner(new FlightBookingScriptedModelProvider(),
    new FlightBookingToolProvider(), new FlightBookingToolExecutor(), new RunLimits(),
    new ConsoleExecutionEventSink(), policies, new InMemoryRunStateStore());

Console.WriteLine("=== Flight Booking Approval ===");
Console.WriteLine("Cancel my booking.");
var paused = await runner.RunAsync(new("Cancel my booking."));
Console.WriteLine($"Status: {paused.Status}");
Console.WriteLine($"RunId: {paused.RunId}");
if (paused.Status != RunStatus.AwaitingApproval || paused.PendingApproval is not { } approval)
{
    Console.WriteLine(paused.Error);
    return 1;
}
Console.WriteLine("Simulating approval...");
var submitted = await runner.SubmitApprovalAsync(new(paused.RunId, approval.ApprovalId, ApprovalChoice.Approve));
Console.WriteLine($"Approval command: {submitted.Status}");
Console.WriteLine($"RunId: {submitted.RunResult?.RunId}");
Console.WriteLine(submitted.RunResult?.FinalText ?? submitted.RunResult?.Error);
return submitted.RunResult?.Status == RunStatus.Completed ? 0 : 1;
