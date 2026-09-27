using System.Text.Json;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Core.Models;
using PortableAgent.Core.Policies;
using PortableAgent.Core.Tools;
using PortableAgent.Core.Tests.TestDoubles;

namespace PortableAgent.Core.Tests;

public sealed class ToolFailureRecoveryTests
{
    private static ToolCall Call(string id) => new(id, "calculator_add", JsonSerializer.SerializeToElement(new { a = 2, b = 3 }));
    private static ModelReply Propose(params ToolCall[] calls) => new(null, calls, ModelFinishReason.ToolCalls);
    private static ModelReply Done() => new("Failure handled.", [], ModelFinishReason.Completed);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_stops_at_failure_and_skipped_call_id_cannot_be_reused(bool reuseSkippedId)
    {
        var model = new RecordingModel(r => r.Messages.Count == 1 ? Propose(Call("A"), Call("B"), Call("C"))
            : reuseSkippedId ? Propose(Call("C")) : Done());
        var executor = new RecordingExecutor { Execute = c => new(c.CallId, c.CallId != "B", Error: c.CallId == "B" ? "Invalid input." : null) };
        var events = new Events();
        var policy = new Policy();
        var result = await new AgentRunner("test", model, new TestToolProvider(), executor, new(), events, policy).RunAsync(new("batch"));
        Assert.Equal(reuseSkippedId ? RunStatus.Failed : RunStatus.Completed, result.Status);
        if (reuseSkippedId) Assert.Contains("unique", result.Error!);
        Assert.Equal(new[] { "A", "B" }, executor.Calls.Select(c => c.Call.CallId));
        Assert.Equal(new[] { "A", "B", "C" }, policy.Calls);
        var results = model.Requests[1].Messages.Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolResult!).ToArray();
        Assert.Equal(new[] { "A", "B", "C" }, results.Select(r => r.CallId));
        Assert.True(results[0].IsSuccess);
        Assert.Equal(ToolResultDisposition.Executed, results[0].Disposition);
        Assert.False(results[1].IsSuccess);
        Assert.Equal(ToolResultDisposition.Executed, results[1].Disposition);
        Assert.False(results[2].IsSuccess);
        Assert.Equal(ToolResultDisposition.NotExecutedDueToPriorFailure, results[2].Disposition);
        Assert.Null(results[2].Output);
        Assert.Contains("earlier", results[2].Error!);
        Assert.Equal(2, events.Items.Count(e => e.EventType == ExecutionEventType.ToolExecutionStarted));
        Assert.Equal(2, events.Items.Count(e => e.EventType == ExecutionEventType.ToolExecutionCompleted));
        Assert.DoesNotContain(events.Items, e => e.EventType is ExecutionEventType.ToolExecutionStarted or ExecutionEventType.ToolExecutionCompleted
            && e.Payload.GetProperty("callId").GetString() == "C");
        var failedIndex = events.Items.FindIndex(e => e.EventType == ExecutionEventType.ToolExecutionCompleted && !e.Payload.GetProperty("success").GetBoolean());
        Assert.Equal(ExecutionEventType.ModelTurnStarted, events.Items[failedIndex + 1].EventType);
        Assert.Equal(2, events.Items.Last().Payload.GetProperty("toolCalls").GetInt32());
    }

    [Theory]
    [InlineData(2, 10, 2, 2)]
    [InlineData(10, 1, 2, 1)]
    public async Task Explicit_repeated_failure_attempts_stop_at_each_budget(int turns, int tools, int expectedTurns, int expectedTools)
    {
        var model = new RecordingModel(r => Propose(Call($"call-{r.Messages.Count}")));
        var executor = new RecordingExecutor { Execute = c => new(c.CallId, false, Error: "Still invalid.") };
        var events = new Events();
        var policy = new Policy();
        var result = await new AgentRunner("test", model, new TestToolProvider(), executor, new(turns, tools), events, policy).RunAsync(new("retry"));
        Assert.Equal(RunStatus.LimitReached, result.Status);
        Assert.Equal(expectedTurns, model.Requests.Count);
        Assert.Equal(expectedTools, executor.Calls.Count);
        Assert.Equal(expectedTools, policy.Calls.Count);
        Assert.Equal(expectedTools, events.Items.Last().Payload.GetProperty("toolCalls").GetInt32());
        Assert.Equal(expectedTools, executor.Calls.Select(c => c.Call.CallId).Distinct().Count());
    }

    [Fact]
    public async Task Skipped_operation_can_be_proposed_with_new_id_using_unconsumed_execution_budget()
    {
        var model = new RecordingModel(r => r.Messages.Count switch
        {
            1 => Propose(Call("A"), Call("B"), Call("C")),
            5 => Propose(Call("C-new")),
            _ => Done()
        });
        var executor = new RecordingExecutor { Execute = c => new(c.CallId, c.CallId != "B") };
        var policy = new Policy();
        var events = new Events();
        var result = await new AgentRunner("test", model, new TestToolProvider(), executor, new(3, 3), events, policy).RunAsync(new("batch retry"));
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(new[] { "A", "B", "C-new" }, executor.Calls.Select(c => c.Call.CallId));
        Assert.Equal(new[] { "A", "B", "C", "C-new" }, policy.Calls);
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(3, events.Items.Last().Payload.GetProperty("toolCalls").GetInt32());
        Assert.Equal(ToolResultDisposition.NotExecutedDueToPriorFailure, model.Requests[1].Messages[^1].ToolResult!.Disposition);
        Assert.Equal("C-new", model.Requests[2].Messages[^1].ToolResult!.CallId);
    }

    [Theory]
    [InlineData("call-id")]
    [InlineData("disposition")]
    [InlineData("exception")]
    public async Task Invalid_results_and_executor_exceptions_do_not_publish_completion_or_continue(string fault)
    {
        var model = new RecordingModel(_ => Propose(Call("A")));
        var executor = new RecordingExecutor { Execute = c => fault switch
        {
            "call-id" => new("corrupt", false),
            "disposition" => new(c.CallId, false, Disposition: ToolResultDisposition.NotExecutedDueToPriorFailure),
            _ => throw new InvalidOperationException("Executor failed.")
        } };
        var events = new Events();
        var result = await new AgentRunner("test", model, new TestToolProvider(), executor, new(), events).RunAsync(new("invalid"));
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Single(model.Requests);
        Assert.Single(executor.Calls);
        Assert.DoesNotContain(events.Items, e => e.EventType == ExecutionEventType.ToolExecutionCompleted);
        Assert.Equal(ExecutionEventType.ToolExecutionStarted, events.Items[^2].EventType);
        Assert.Equal(ExecutionEventType.RunFailed, events.Items[^1].EventType);
    }

    [Fact]
    public async Task Model_created_reattempt_rechecks_policy_and_can_be_denied()
    {
        var model = new RecordingModel(r => r.Messages.Count switch { 1 => Propose(Call("A")), 3 => Propose(Call("B")), _ => Done() });
        var executor = new RecordingExecutor { Execute = c => new(c.CallId, false, Error: "Failed.") };
        var policy = new Policy(denyAfterFirst: true);
        var result = await new AgentRunner("test", model, new TestToolProvider(), executor, new(), policyEvaluator: policy).RunAsync(new("retry"));
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Single(executor.Calls);
        Assert.Equal(new[] { "A", "B" }, policy.Calls);
        Assert.Equal(ToolResultDisposition.DeniedByPolicy, model.Requests[^1].Messages[^1].ToolResult!.Disposition);
    }

    private sealed class Events : IExecutionEventSink
    {
        public List<ExecutionEvent> Items { get; } = [];
        public ValueTask PublishAsync(ExecutionEvent e, CancellationToken ct) { Items.Add(e); return ValueTask.CompletedTask; }
    }
    private sealed class Policy(bool denyAfterFirst = false) : IPolicyEvaluator
    {
        public List<string> Calls { get; } = [];
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyEvaluationContext context, CancellationToken ct)
        {
            Calls.Add(context.Call.CallId);
            return ValueTask.FromResult(new PolicyDecision(denyAfterFirst && Calls.Count > 1 ? PolicyOutcome.Deny : PolicyOutcome.Allow));
        }
    }
}
