using System.Text.Json;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Core.Models;
using PortableAgent.Core.Policies;
using PortableAgent.Core.Tools;
using PortableAgent.Infrastructure.Models;

namespace PortableAgent.Adapters.Mcp.Tests;

public sealed class RemoteApprovalTests
{
    private static async Task<AgentRunResult> PauseAsync(DomainServerFixture server, TestDatabase db)
    {
        // The adapter and all execution objects from the first composition are abandoned on return.
        await using var adapter = new McpToolAdapter(new("flight-mcp", server.Endpoint));
        var result = await DomainPortabilityTests.Runner(adapter, db.Path).RunAsync(new("Cancel my booking."));
        Assert.Equal(RunStatus.AwaitingApproval, result.Status);
        Assert.NotNull(result.PendingApproval);
        AssertNoCancellation(server);
        var state = (await db.Store.GetAsync(result.RunId, default))!;
        Assert.Equal(RunLifecycleState.AwaitingApproval, state.Lifecycle);
        Assert.Equal(new ToolId("flight-mcp", "cancel_booking"), state.PendingApproval!.ToolDefinition.Id);
        Assert.Equal("confirm-flight-cancellation-v1", state.PendingApproval.PolicyId);
        Assert.Equal(1, state.ModelTurns);
        Assert.Equal(0, state.ToolCalls);
        Assert.Equal(1, server.Discoveries);
        var events = await db.Store.ReadEventsAfterAsync(result.RunId, 0, 100, default);
        Assert.Equal(ExecutionEventType.ApprovalRequired, events[^1].EventType);
        Assert.DoesNotContain(events, e => e.EventType is ExecutionEventType.ToolExecutionStarted or ExecutionEventType.ToolExecutionCompleted);
        return result;
    }

    private static void AssertNoCancellation(DomainServerFixture server)
    {
        Assert.Empty(server.Calls("cancel_booking"));
        Assert.Equal(0, server.Flight.CancelCallCount);
        Assert.Equal(0, server.Flight.CancelMutationCount);
        Assert.Equal("confirmed", server.Flight.GetBooking("NZ123").Status);
    }

    [Fact]
    public async Task Pause_persists_approval_without_sending_remote_write()
    {
        await using var server = await DomainServerFixture.StartAsync();
        using var db = await TestDatabase.CreateAsync();
        await PauseAsync(server, db);
    }

    [Fact]
    public async Task Fresh_runtime_approves_exact_frozen_call_before_model_and_preserves_event_continuity()
    {
        await using var server = await DomainServerFixture.StartAsync();
        using var db = await TestDatabase.CreateAsync();
        var paused = await PauseAsync(server, db);
        var frozen = (await db.Store.GetAsync(paused.RunId, default))!.PendingApproval!;
        var model = new ObserveModel(() =>
        {
            Assert.Single(server.Calls("cancel_booking"));
            Assert.Equal("cancelled", server.Flight.GetBooking("NZ123").Status);
        });
        await using var freshAdapter = new McpToolAdapter(new("flight-mcp", server.Endpoint));
        var runner = DomainPortabilityTests.Runner(freshAdapter, db.Path, model: model);
        var submitted = await runner.SubmitApprovalAsync(new(paused.RunId, frozen.ApprovalId, ApprovalChoice.Approve));
        Assert.Equal(ApprovalSubmissionStatus.Accepted, submitted.Status);
        Assert.Equal(RunStatus.Completed, submitted.RunResult!.Status);
        Assert.Equal(paused.RunId, submitted.RunResult.RunId);
        Assert.Equal("Booking NZ123 has been cancelled.", submitted.RunResult.FinalText);
        Assert.Equal(1, model.Turns); // Only the final model turn; no regenerated proposal.
        Assert.Equal(2, server.Discoveries);
        Assert.Equal(1, server.Flight.CancelCallCount);
        Assert.Equal(1, server.Flight.CancelMutationCount);
        var request = Assert.Single(server.Calls("cancel_booking"));
        var parameters = request.GetProperty("params");
        Assert.Equal(frozen.ToolCall.ToolName, parameters.GetProperty("name").GetString());
        Assert.True(JsonElement.DeepEquals(frozen.ToolCall.Arguments, parameters.GetProperty("arguments")));
        foreach (var forbidden in new[] { "RunId", "ApprovalId", "RuntimeDefinitionId", "PolicyId", "call-1" })
            Assert.False(request.GetRawText().Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        var state = (await db.Store.GetAsync(paused.RunId, default))!;
        Assert.Equal(RunLifecycleState.Completed, state.Lifecycle);
        Assert.Equal(frozen.ToolCall.CallId, state.Conversation[2].ToolResult!.CallId);
        Assert.True(JsonElement.DeepEquals(frozen.ToolCall.Arguments, state.Conversation[1].ToolCalls[0].Arguments));
        var events = await db.Store.ReadEventsAfterAsync(paused.RunId, 0, 100, default);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.All(events, e => Assert.Equal(paused.RunId, e.RunId));
        Assert.Single(events, e => e.EventType == ExecutionEventType.RunStarted);
        Assert.Equal(state.LastSequence, events[^1].Sequence);
        Assert.Equal(new[]
        {
            ExecutionEventType.ApprovalResolved, ExecutionEventType.RunResumed,
            ExecutionEventType.ToolDiscoveryStarted, ExecutionEventType.ToolDiscoveryCompleted,
            ExecutionEventType.PolicyEvaluationStarted, ExecutionEventType.PolicyEvaluationCompleted,
            ExecutionEventType.ToolExecutionStarted, ExecutionEventType.ToolExecutionCompleted,
            ExecutionEventType.ModelTurnStarted, ExecutionEventType.ModelTurnCompleted, ExecutionEventType.RunCompleted
        }, events.Skip(9).Select(e => e.EventType));
        Assert.Equal(2, state.ModelTurns);
        Assert.Equal(1, state.ToolCalls);

        // A duplicate approval in another fresh composition cannot cause a second request.
        await using var duplicateAdapter = new McpToolAdapter(new("flight-mcp", server.Endpoint));
        var duplicate = await DomainPortabilityTests.Runner(duplicateAdapter, db.Path)
            .SubmitApprovalAsync(new(paused.RunId, frozen.ApprovalId, ApprovalChoice.Approve));
        Assert.Equal(ApprovalSubmissionStatus.Conflict, duplicate.Status);
        Assert.Single(server.Calls("cancel_booking"));
        Assert.Equal(2, server.Discoveries);
        Assert.Equal(1, server.Flight.CancelCallCount);
        Assert.Equal(1, server.Flight.CancelMutationCount);
    }

    [Fact]
    public async Task Fresh_runtime_rejection_rediscovers_but_never_invokes_remote_business_operation()
    {
        await using var server = await DomainServerFixture.StartAsync();
        using var db = await TestDatabase.CreateAsync();
        var paused = await PauseAsync(server, db);
        await using var adapter = new McpToolAdapter(new("flight-mcp", server.Endpoint));
        var result = await DomainPortabilityTests.Runner(adapter, db.Path)
            .SubmitApprovalAsync(new(paused.RunId, paused.PendingApproval!.ApprovalId, ApprovalChoice.Reject));
        Assert.Equal(ApprovalSubmissionStatus.Accepted, result.Status);
        Assert.Equal(RunStatus.Completed, result.RunResult!.Status);
        Assert.Equal(paused.RunId, result.RunResult.RunId);
        Assert.Equal("Understood. I did not cancel the booking.", result.RunResult.FinalText);
        Assert.Equal(2, server.Discoveries);
        AssertNoCancellation(server);
        var state = (await db.Store.GetAsync(paused.RunId, default))!;
        Assert.Equal(ToolResultDisposition.RejectedByUser, state.Conversation[2].ToolResult!.Disposition);
        var events = await db.Store.ReadEventsAfterAsync(paused.RunId, 0, 100, default);
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.Single(events, e => e.EventType == ExecutionEventType.RunStarted);
        Assert.All(events, e => Assert.Equal(paused.RunId, e.RunId));
        Assert.DoesNotContain(events, e => e.EventType is ExecutionEventType.ToolExecutionStarted or ExecutionEventType.ToolExecutionCompleted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fresh_discovery_of_changed_schema_or_missing_tool_fails_closed(bool remove)
    {
        await using var server = await DomainServerFixture.StartAsync();
        using var db = await TestDatabase.CreateAsync();
        var paused = await PauseAsync(server, db);
        var cancel = server.Catalog.Single(t => t.Name == "cancel_booking");
        if (remove) server.Catalog.Remove(cancel);
        else cancel.InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { bookingId = new { type = "integer" } } });
        await using var adapter = new McpToolAdapter(new("flight-mcp", server.Endpoint));
        var result = await DomainPortabilityTests.Runner(adapter, db.Path)
            .SubmitApprovalAsync(new(paused.RunId, paused.PendingApproval!.ApprovalId, ApprovalChoice.Approve));
        Assert.Equal(RunStatus.Failed, result.RunResult!.Status);
        Assert.Contains("contract has changed", result.RunResult.Error);
        Assert.Equal(2, server.Discoveries);
        AssertNoCancellation(server);
    }

    [Fact]
    public async Task Changed_policy_to_deny_returns_denial_to_model_without_remote_call()
    {
        await using var server = await DomainServerFixture.StartAsync();
        using var db = await TestDatabase.CreateAsync();
        var paused = await PauseAsync(server, db);
        await using var adapter = new McpToolAdapter(new("flight-mcp", server.Endpoint));
        var result = await DomainPortabilityTests.Runner(adapter, db.Path, cancellation: new(PolicyOutcome.Deny))
            .SubmitApprovalAsync(new(paused.RunId, paused.PendingApproval!.ApprovalId, ApprovalChoice.Approve));
        Assert.Equal(RunStatus.Completed, result.RunResult!.Status);
        Assert.Equal(2, server.Discoveries);
        AssertNoCancellation(server);
        var state = (await db.Store.GetAsync(paused.RunId, default))!;
        Assert.Equal(ToolResultDisposition.DeniedByPolicy, state.Conversation[2].ToolResult!.Disposition);
        Assert.Equal("The booking was not cancelled because the operation was not permitted.", result.RunResult.FinalText);
    }

    [Fact]
    public async Task Changed_policy_identity_requires_new_approval_before_exact_frozen_call_can_execute()
    {
        await using var server = await DomainServerFixture.StartAsync();
        using var db = await TestDatabase.CreateAsync();
        var paused = await PauseAsync(server, db);
        var changed = new PolicyDecision(PolicyOutcome.RequireApproval, "confirm-flight-cancellation-v2");
        AgentRunResult again;
        await using (var adapter = new McpToolAdapter(new("flight-mcp", server.Endpoint)))
        {
            var result = await DomainPortabilityTests.Runner(adapter, db.Path, cancellation: changed)
                .SubmitApprovalAsync(new(paused.RunId, paused.PendingApproval!.ApprovalId, ApprovalChoice.Approve));
            again = result.RunResult!;
            Assert.Equal(RunStatus.AwaitingApproval, again.Status);
        }
        Assert.Equal(paused.RunId, again.RunId);
        Assert.NotEqual(paused.PendingApproval!.ApprovalId, again.PendingApproval!.ApprovalId);
        Assert.Equal(changed.PolicyId, again.PendingApproval.PolicyId);
        Assert.True(JsonElement.DeepEquals(paused.PendingApproval.ToolCall.Arguments, again.PendingApproval.ToolCall.Arguments));
        Assert.Equal(2, server.Discoveries);
        AssertNoCancellation(server);
        await using var fresh = new McpToolAdapter(new("flight-mcp", server.Endpoint));
        var completed = await DomainPortabilityTests.Runner(fresh, db.Path, cancellation: changed)
            .SubmitApprovalAsync(new(again.RunId, again.PendingApproval.ApprovalId, ApprovalChoice.Approve));
        Assert.Equal(RunStatus.Completed, completed.RunResult!.Status);
        Assert.Single(server.Calls("cancel_booking"));
        Assert.Equal(1, server.Flight.CancelCallCount);
        Assert.Equal(1, server.Flight.CancelMutationCount);
        Assert.Equal(3, server.Discoveries);
    }

    private sealed class ObserveModel(Action beforeGenerate) : IModelProvider
    {
        private readonly FlightBookingScriptedModelProvider _inner = new();
        public int Turns { get; private set; }
        public Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken ct)
        {
            beforeGenerate();
            Turns++;
            return _inner.GenerateAsync(request, ct);
        }
    }
}
