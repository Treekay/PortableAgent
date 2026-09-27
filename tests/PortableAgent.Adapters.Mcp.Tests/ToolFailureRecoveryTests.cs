using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;
using PortableAgent.Sample.FlightBookingMcpServer;

namespace PortableAgent.Adapters.Mcp.Tests;

public sealed class ToolFailureRecoveryTests
{
    private static ToolCall Call(string id, string booking = "UNKNOWN", string name = "get_booking") =>
        new(id, name, JsonSerializer.SerializeToElement(new { bookingId = booking }));
    private static ModelReply Propose(params ToolCall[] calls) => new(null, calls, ModelFinishReason.ToolCalls);
    private static ModelReply Done(string text = "I could not find that booking.") => new(text, [], ModelFinishReason.Completed);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_business_error_is_persisted_and_model_may_explicitly_correct_arguments(bool correct)
    {
        await using var server = await DomainServerFixture.StartAsync();
        using var db = await TestDatabase.CreateAsync();
        var model = new Script(r =>
        {
            if (r.Messages.Count == 1) return Propose(Call("call-1", correct ? "WRONG" : "UNKNOWN"));
            var result = r.Messages[^1].ToolResult!;
            if (result.CallId == "call-1")
            {
                Assert.False(result.IsSuccess);
                Assert.Equal(ToolResultDisposition.Executed, result.Disposition);
                Assert.Equal("Unknown booking.", result.Error);
                Assert.NotNull(result.Output);
                Assert.Single(server.Calls("get_booking"));
                return correct ? Propose(Call("call-2", "NZ123")) : Done();
            }
            Assert.Equal("call-2", result.CallId);
            Assert.True(result.IsSuccess);
            Assert.Equal("confirmed", result.Output!.Value.GetProperty("status").GetString());
            return Done("Booking NZ123 is confirmed.");
        });
        await using var adapter = new McpToolAdapter(new("flight-mcp", server.Endpoint));
        var run = await DomainPortabilityTests.Runner(adapter, db.Path, model: model).RunAsync(new("lookup"));
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(correct ? "Booking NZ123 is confirmed." : "I could not find that booking.", run.FinalText);
        var state = (await db.Store.GetAsync(run.RunId, default))!;
        Assert.Equal(correct ? 3 : 2, state.ModelTurns);
        Assert.Equal(state.ModelTurns, model.Requests.Count);
        Assert.Equal(correct ? 2 : 1, state.ToolCalls);
        Assert.Equal(state.ToolCalls, server.Calls("get_booking").Length);
        Assert.Equal(state.ToolCalls, state.UsedCallIds.Count);
        var savedFailure = state.Conversation[2].ToolResult!;
        Assert.Equal("call-1", savedFailure.CallId);
        Assert.False(savedFailure.IsSuccess);
        Assert.Equal("Unknown booking.", savedFailure.Error);
        Assert.NotNull(savedFailure.Output);
        var events = await db.Store.ReadEventsAfterAsync(run.RunId, 0, 100, default);
        Assert.Equal(state.ToolCalls, events.Count(e => e.EventType == ExecutionEventType.PolicyEvaluationStarted));
        var failedIndex = events.ToList().FindIndex(e => e.EventType == ExecutionEventType.ToolExecutionCompleted && !e.Payload.GetProperty("success").GetBoolean());
        Assert.Equal(ExecutionEventType.ToolExecutionStarted, events[failedIndex - 1].EventType);
        Assert.Equal(ExecutionEventType.ModelTurnStarted, events[failedIndex + 1].EventType);
        Assert.Equal(ExecutionEventType.RunCompleted, events[^1].EventType);
        var arguments = server.Calls("get_booking").Select(r => r.GetProperty("params").GetProperty("arguments").GetProperty("bookingId").GetString());
        Assert.Equal(correct ? ["WRONG", "NZ123"] : new[] { "UNKNOWN" }, arguments);
    }

    [Fact]
    public async Task Real_batch_stops_after_failure_and_persists_all_correlated_results()
    {
        await using var server = await DomainServerFixture.StartAsync();
        using var db = await TestDatabase.CreateAsync();
        var model = new Script(r => r.Messages.Count == 1
            ? Propose(Call("A", "NZ123"), Call("B"), Call("C", "NZ123")) : Done("The second lookup failed; the third was skipped."));
        await using var adapter = new McpToolAdapter(new("flight-mcp", server.Endpoint));
        var run = await DomainPortabilityTests.Runner(adapter, db.Path, model: model).RunAsync(new("batch"));
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(2, server.Calls("get_booking").Length);
        var state = (await db.Store.GetAsync(run.RunId, default))!;
        Assert.Equal(2, state.ToolCalls);
        Assert.Equal(2, state.ModelTurns);
        Assert.Equal(3, state.UsedCallIds.Count);
        var results = state.Conversation.Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolResult!).ToArray();
        Assert.Equal(new[] { "A", "B", "C" }, results.Select(r => r.CallId));
        Assert.True(results[0].IsSuccess);
        Assert.Equal(ToolResultDisposition.Executed, results[0].Disposition);
        Assert.False(results[1].IsSuccess);
        Assert.Equal(ToolResultDisposition.Executed, results[1].Disposition);
        Assert.Equal(ToolResultDisposition.NotExecutedDueToPriorFailure, results[2].Disposition);
        Assert.False(results[2].IsSuccess);
        Assert.Null(results[2].Output);
        Assert.Contains("earlier", results[2].Error!);
        Assert.Equal(5, model.Requests[1].Messages.Count);
        var events = await db.Store.ReadEventsAfterAsync(run.RunId, 0, 100, default);
        Assert.Equal(3, events.Count(e => e.EventType == ExecutionEventType.PolicyEvaluationStarted));
        Assert.Equal(new[] { "A", "B" }, events.Where(e => e.EventType == ExecutionEventType.ToolExecutionStarted).Select(e => e.Payload.GetProperty("callId").GetString()));
        Assert.Equal(new[] { "A", "B" }, events.Where(e => e.EventType == ExecutionEventType.ToolExecutionCompleted).Select(e => e.Payload.GetProperty("callId").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approved_write_failure_consumes_approval_and_new_model_attempt_requires_new_approval(bool retry)
    {
        await using var server = await DomainServerFixture.StartAsync();
        using var db = await TestDatabase.CreateAsync();
        server.CallOverride = _ => new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "Cancellation temporarily unavailable." }],
            StructuredContent = JsonSerializer.SerializeToElement(new { code = "cancellation_unavailable" })
        };
        AgentRunResult paused;
        await using (var first = new McpToolAdapter(new("flight-mcp", server.Endpoint)))
        {
            var initialModel = new Script(_ => Propose(Call("write-1", "NZ123", "cancel_booking")));
            paused = await DomainPortabilityTests.Runner(first, db.Path, model: initialModel).RunAsync(new("cancel"));
        }
        Assert.Equal(RunStatus.AwaitingApproval, paused.Status);
        Assert.Empty(server.Calls("cancel_booking"));
        var originalApproval = paused.PendingApproval!;
        var model = new Script(r =>
        {
            var failure = r.Messages[^1].ToolResult!;
            Assert.Equal("write-1", failure.CallId);
            Assert.False(failure.IsSuccess);
            Assert.Equal("Cancellation temporarily unavailable.", failure.Error);
            Assert.Single(server.Calls("cancel_booking"));
            return retry ? Propose(Call("write-2", "NZ123", "cancel_booking")) : Done("Cancellation was not available.");
        });
        await using (var fresh = new McpToolAdapter(new("flight-mcp", server.Endpoint)))
        {
            var submission = await DomainPortabilityTests.Runner(fresh, db.Path, model: model)
                .SubmitApprovalAsync(new(paused.RunId, originalApproval.ApprovalId, ApprovalChoice.Approve));
            Assert.Equal(ApprovalSubmissionStatus.Accepted, submission.Status);
            Assert.Equal(retry ? RunStatus.AwaitingApproval : RunStatus.Completed, submission.RunResult!.Status);
            Assert.Equal(paused.RunId, submission.RunResult.RunId);
        }
        Assert.Single(model.Requests);
        Assert.Single(server.Calls("cancel_booking"));
        var state = (await db.Store.GetAsync(paused.RunId, default))!;
        Assert.Equal(1, state.ToolCalls);
        Assert.Equal(2, state.ModelTurns);
        Assert.Equal(ApprovalStatus.Approved, Assert.Single(state.ResolvedApprovals).Status);
        Assert.Equal(originalApproval.ApprovalId, state.ResolvedApprovals[0].ApprovalId);
        var failureResult = state.Conversation[2].ToolResult!;
        Assert.False(failureResult.IsSuccess);
        Assert.Equal(ToolResultDisposition.Executed, failureResult.Disposition);
        Assert.Equal("cancellation_unavailable", failureResult.Output!.Value.GetProperty("code").GetString());
        if (retry)
        {
            Assert.NotEqual(originalApproval.ApprovalId, state.PendingApproval!.ApprovalId);
            Assert.Equal(originalApproval.PolicyId, state.PendingApproval.PolicyId);
            Assert.Equal("write-2", state.PendingApproval.ToolCall.CallId);
            Assert.Equal(new[] { "write-1", "write-2" }, state.UsedCallIds.Order());
        }
        else Assert.Null(state.PendingApproval);
        var events = await db.Store.ReadEventsAfterAsync(paused.RunId, 0, 100, default);
        Assert.Equal(retry ? 3 : 2, events.Count(e => e.EventType == ExecutionEventType.PolicyEvaluationStarted));
        Assert.Single(events, e => e.EventType == ExecutionEventType.ToolExecutionCompleted && !e.Payload.GetProperty("success").GetBoolean());

        await using var duplicate = new McpToolAdapter(new("flight-mcp", server.Endpoint));
        Assert.Equal(ApprovalSubmissionStatus.Conflict,
            (await DomainPortabilityTests.Runner(duplicate, db.Path, model: model)
                .SubmitApprovalAsync(new(paused.RunId, originalApproval.ApprovalId, ApprovalChoice.Approve))).Status);
        Assert.Single(server.Calls("cancel_booking"));
        if (retry)
        {
            // Yet another fresh Runtime must approve the second frozen operation separately.
            server.CallOverride = new FlightBookingTools(server.Flight).Call;
            var finalModel = new Script(r =>
            {
                Assert.Equal("write-2", r.Messages[^1].ToolResult!.CallId);
                Assert.True(r.Messages[^1].ToolResult!.IsSuccess);
                return Done("The new cancellation attempt succeeded.");
            });
            var completed = await DomainPortabilityTests.Runner(duplicate, db.Path, model: finalModel)
                .SubmitApprovalAsync(new(paused.RunId, state.PendingApproval!.ApprovalId, ApprovalChoice.Approve));
            Assert.Equal(RunStatus.Completed, completed.RunResult!.Status);
            Assert.Equal(2, server.Calls("cancel_booking").Length);
            Assert.Equal(1, server.Flight.CancelMutationCount);
            var saved = (await db.Store.GetAsync(paused.RunId, default))!;
            Assert.Equal(2, saved.ToolCalls);
            Assert.Equal(3, saved.ModelTurns);
            Assert.Equal(2, saved.ResolvedApprovals.Count);
        }
    }

    [Fact]
    public async Task Remote_mutation_then_protocol_failure_fails_run_without_replay_or_fake_tool_result()
    {
        await using var server = await DomainServerFixture.StartAsync();
        using var db = await TestDatabase.CreateAsync();
        server.CallOverride = request =>
        {
            server.Flight.CancelBooking(request.Arguments!["bookingId"].GetString()!);
            throw new McpProtocolException("Response unavailable after business operation.", McpErrorCode.InternalError);
        };
        var model = new Script(_ => Propose(Call("write-1", "NZ123", "cancel_booking")));
        AgentRunResult paused;
        await using (var first = new McpToolAdapter(new("flight-mcp", server.Endpoint)))
            paused = await DomainPortabilityTests.Runner(first, db.Path, model: model).RunAsync(new("cancel"));
        await using var fresh = new McpToolAdapter(new("flight-mcp", server.Endpoint));
        var resumedModel = new Script(_ => throw new InvalidOperationException("Model must not be called after protocol failure."));
        var failed = await DomainPortabilityTests.Runner(fresh, db.Path, model: resumedModel)
            .SubmitApprovalAsync(new(paused.RunId, paused.PendingApproval!.ApprovalId, ApprovalChoice.Approve));
        Assert.Equal(RunStatus.Failed, failed.RunResult!.Status);
        Assert.Empty(resumedModel.Requests);
        Assert.Single(server.Calls("cancel_booking"));
        Assert.Equal(1, server.Flight.CancelCallCount);
        Assert.Equal(1, server.Flight.CancelMutationCount);
        Assert.Equal("cancelled", server.Flight.GetBooking("NZ123").Status);
        var state = (await db.Store.GetAsync(paused.RunId, default))!;
        Assert.Equal(RunLifecycleState.Failed, state.Lifecycle);
        Assert.Equal(1, state.ToolCalls);
        Assert.DoesNotContain(state.Conversation, m => m.Role == MessageRole.Tool);
        var events = await db.Store.ReadEventsAfterAsync(paused.RunId, 0, 100, default);
        Assert.DoesNotContain(events, e => e.EventType == ExecutionEventType.ToolExecutionCompleted);
        Assert.Equal(ExecutionEventType.ToolExecutionStarted, events[^2].EventType);
        Assert.Equal(ExecutionEventType.RunFailed, events[^1].EventType);
    }

    private sealed class Script(Func<ModelRequest, ModelReply> respond) : IModelProvider
    {
        public List<ModelRequest> Requests { get; } = [];
        public Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
