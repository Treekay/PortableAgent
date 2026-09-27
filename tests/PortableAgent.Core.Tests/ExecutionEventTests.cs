using System.Collections.Concurrent;
using System.Text.Json;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;
using PortableAgent.Core.Tests.TestDoubles;

namespace PortableAgent.Core.Tests;

public sealed class ExecutionEventTests
{
    private static ToolCall Call(string name = "calculator_add") =>
        new("call-1", name, JsonSerializer.SerializeToElement(new { secretArgument = "argument-secret" }));

    private static ModelReply Propose(string name = "calculator_add") =>
        new("private-model-text", [Call(name)], ModelFinishReason.ToolCalls);

    private static RecordingModel SuccessfulModel() => new(request => request.Messages.Count == 1
        ? Propose() : new("private-final-answer", [], ModelFinishReason.Completed));

    [Fact]
    public async Task Successful_run_has_ordered_correlated_events_and_only_selected_metadata()
    {
        var sink = new RecordingSink();
        var model = SuccessfulModel();
        var executor = new RecordingExecutor
        {
            Execute = call => new(call.CallId, true, JsonSerializer.SerializeToElement(new { secret = "output-secret" }))
        };
        var runner = new AgentRunner(model, new TestToolProvider(), executor, new(), sink);
        var before = DateTimeOffset.UtcNow;
        var result = await runner.RunAsync(new("private-user-message"));
        var after = DateTimeOffset.UtcNow;
        var events = sink.Events.ToArray();

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.NotEqual(Guid.Empty, result.RunId);
        Assert.Equal(new[]
        {
            ExecutionEventType.RunStarted,
            ExecutionEventType.ToolDiscoveryStarted,
            ExecutionEventType.ToolDiscoveryCompleted,
            ExecutionEventType.ModelTurnStarted,
            ExecutionEventType.ModelTurnCompleted,
            ExecutionEventType.ToolCallProposed,
            ExecutionEventType.PolicyEvaluationStarted,
            ExecutionEventType.PolicyEvaluationCompleted,
            ExecutionEventType.ToolExecutionStarted,
            ExecutionEventType.ToolExecutionCompleted,
            ExecutionEventType.ModelTurnStarted,
            ExecutionEventType.ModelTurnCompleted,
            ExecutionEventType.RunCompleted
        }, events.Select(e => e.EventType));
        Assert.Equal(Enumerable.Range(1, events.Length).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.Equal(events.Length, events.Select(e => e.EventId).Distinct().Count());
        Assert.All(events, e =>
        {
            Assert.Equal(result.RunId, e.RunId);
            Assert.NotEqual(Guid.Empty, e.EventId);
            Assert.InRange(e.OccurredAt, before, after);
            Assert.Equal(TimeSpan.Zero, e.OccurredAt.Offset);
        });
        Assert.Equal(1, events[2].Payload.GetProperty("toolCount").GetInt32());
        Assert.Equal("calculator_add", events[2].Payload.GetProperty("tools")[0].GetString());
        Assert.Equal(1, events[3].Payload.GetProperty("turn").GetInt32());
        Assert.Equal("ToolCalls", events[4].Payload.GetProperty("finishReason").GetString());
        Assert.Equal(1, events[4].Payload.GetProperty("toolCallCount").GetInt32());
        Assert.Equal("call-1", events[5].Payload.GetProperty("callId").GetString());
        Assert.Equal("trusted-local/calculator.add", events[8].Payload.GetProperty("toolId").GetString());
        Assert.True(events[9].Payload.GetProperty("success").GetBoolean());
        Assert.Equal(2, events[10].Payload.GetProperty("turn").GetInt32());
        Assert.Equal("Completed", events[12].Payload.GetProperty("status").GetString());
        Assert.Equal(2, events[12].Payload.GetProperty("modelTurns").GetInt32());
        Assert.Equal(1, events[12].Payload.GetProperty("toolCalls").GetInt32());
        var json = string.Join("\n", events.Select(e => e.Payload.GetRawText()));
        foreach (var secret in new[] { "argument-secret", "output-secret", "private-model-text", "private-final-answer", "private-user-message" })
            Assert.DoesNotContain(secret, json);
        Assert.Equal(2, model.Requests.Count);
        Assert.Single(executor.Calls);
    }

    [Theory]
    [InlineData("unknown", RunStatus.Failed, ExecutionEventType.RunFailed, 0)]
    [InlineData("failure", RunStatus.Failed, ExecutionEventType.RunFailed, 1)]
    [InlineData("correlation", RunStatus.Failed, ExecutionEventType.RunFailed, 1)]
    [InlineData("limit", RunStatus.LimitReached, ExecutionEventType.RunLimitReached, 0)]
    [InlineData("cancelled", RunStatus.Cancelled, ExecutionEventType.RunCancelled, 0)]
    [InlineData("exception", RunStatus.Failed, ExecutionEventType.RunFailed, 0)]
    [InlineData("tool-exception", RunStatus.Failed, ExecutionEventType.RunFailed, 1)]
    public async Task Every_exit_attempts_exactly_one_matching_terminal_event(
        string scenario, RunStatus status, ExecutionEventType terminal, int toolCalls)
    {
        var sink = new RecordingSink();
        var model = new RecordingModel(_ => scenario == "exception"
            ? throw new InvalidOperationException("sensitive-exception")
            : Propose(scenario == "unknown" ? "not-registered" : "calculator_add"));
        var executor = new RecordingExecutor
        {
            Execute = call => scenario switch
            {
                "failure" => new(call.CallId, false, Error: "sensitive-tool-error"),
                "correlation" => new("wrong-id", true),
                "tool-exception" => throw new InvalidOperationException("sensitive-tool-error"),
                _ => new(call.CallId, true)
            }
        };
        var tools = new TestToolProvider();
        var runner = new AgentRunner(model, tools, executor, new(4, scenario == "limit" ? 0 : 4), sink);
        var result = await runner.RunAsync(new("input"), new CancellationToken(scenario == "cancelled"));
        var events = sink.Events.ToArray();

        Assert.Equal(status, result.Status);
        Assert.Equal(result.RunId, Assert.Single(events, e => IsTerminal(e.EventType)).RunId);
        Assert.Equal(terminal, events[^1].EventType);
        Assert.Equal(toolCalls, executor.Calls.Count);
        Assert.Equal(toolCalls, events.Count(e => e.EventType == ExecutionEventType.ToolExecutionStarted));
        Assert.DoesNotContain("sensitive-", string.Join("", events.Select(e => e.Payload.GetRawText())));
        if (scenario == "cancelled")
        {
            Assert.Single(events); // The sink respects the cancelled token for RunStarted, but receives RunCancelled.
            Assert.Equal(0, tools.Calls);
            Assert.Empty(model.Requests);
        }
        if (scenario == "failure")
            Assert.False(Assert.Single(events, e => e.EventType == ExecutionEventType.ToolExecutionCompleted)
                .Payload.GetProperty("success").GetBoolean());
        if (scenario == "unknown")
        {
            Assert.Equal(ExecutionEventType.ToolCallProposed, events[^2].EventType);
            Assert.DoesNotContain(events, e => e.EventType == ExecutionEventType.ToolExecutionCompleted);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Observer_exceptions_do_not_change_decisions_or_stop_later_delivery_attempts(bool cancelException)
    {
        var baselineModel = SuccessfulModel();
        var baselineExecutor = new RecordingExecutor();
        var baseline = await new AgentRunner(baselineModel, new TestToolProvider(), baselineExecutor, new())
            .RunAsync(new("input"));
        var sink = new ThrowingSink(cancelException);
        var model = SuccessfulModel();
        var executor = new RecordingExecutor();
        var observed = await new AgentRunner(model, new TestToolProvider(), executor, new(), sink)
            .RunAsync(new("input"));

        Assert.Equal(baseline.Status, observed.Status);
        Assert.Equal(baseline.FinalText, observed.FinalText);
        Assert.Equal(baseline.Error, observed.Error);
        Assert.Equal(baselineModel.Requests.Count, model.Requests.Count);
        Assert.Equal(baselineExecutor.Calls.Count, executor.Calls.Count);
        Assert.Equal(13, sink.Attempts);
    }

    [Fact]
    public async Task Concurrent_runs_on_same_runner_have_independent_ids_and_sequences()
    {
        var sink = new RecordingSink();
        var runner = new AgentRunner(new FinalModel(), new TestToolProvider(), new RecordingExecutor(), new(), sink);
        var runs = await Task.WhenAll(runner.RunAsync(new("first")), runner.RunAsync(new("second")));
        Assert.NotEqual(runs[0].RunId, runs[1].RunId);
        foreach (var run in runs)
        {
            var events = sink.Events.Where(e => e.RunId == run.RunId).ToArray();
            Assert.Equal(RunStatus.Completed, run.Status);
            Assert.Equal(Enumerable.Range(1, 6).Select(i => (long)i), events.Select(e => e.Sequence));
            Assert.Equal(ExecutionEventType.RunStarted, events[0].EventType);
            Assert.Equal(ExecutionEventType.RunCompleted, events[^1].EventType);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Normal_events_receive_run_token_but_terminal_events_receive_none(bool preCancelled)
    {
        using var source = new CancellationTokenSource();
        if (preCancelled) source.Cancel();
        var sink = new TokenRecordingSink();
        var runner = new AgentRunner(new FinalModel(), new TestToolProvider(), new RecordingExecutor(), new(), sink);

        var result = await runner.RunAsync(new("input"), source.Token);

        Assert.Equal(preCancelled ? RunStatus.Cancelled : RunStatus.Completed, result.Status);
        Assert.NotEqual(Guid.Empty, result.RunId);
        Assert.All(sink.Attempts, attempt => Assert.Equal(
            IsTerminal(attempt.Event.EventType) ? CancellationToken.None : source.Token, attempt.Token));
        var terminal = sink.Attempts[^1];
        Assert.Equal(preCancelled ? ExecutionEventType.RunCancelled : ExecutionEventType.RunCompleted, terminal.Event.EventType);
        Assert.Equal(result.RunId, terminal.Event.RunId);
    }

    [Fact]
    public async Task Cancellation_during_model_call_still_delivers_terminal_event()
    {
        using var source = new CancellationTokenSource();
        var model = new RecordingModel(_ =>
        {
            source.Cancel();
            return new ModelReply("Not a final Run result after cancellation.", [], ModelFinishReason.Completed);
        });
        var sink = new TokenRecordingSink();
        var result = await new AgentRunner(model, new TestToolProvider(), new RecordingExecutor(), new(), sink)
            .RunAsync(new("input"), source.Token);

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Equal(ExecutionEventType.RunCancelled, sink.Attempts[^1].Event.EventType);
        Assert.Equal(CancellationToken.None, sink.Attempts[^1].Token);
        Assert.DoesNotContain(sink.Attempts, attempt => attempt.Event.EventType == ExecutionEventType.RunCompleted);
    }

    [Fact]
    public async Task Failed_delivery_is_not_retried_and_its_sequence_is_not_reused()
    {
        var sink = new FailOnceSink();
        var result = await new AgentRunner(new FinalModel(), new TestToolProvider(), new RecordingExecutor(), new(), sink)
            .RunAsync(new("input"));

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, sink.Attempted);
        Assert.Equal(new long[] { 1, 3, 4, 5, 6 }, sink.Delivered);
    }

    [Fact]
    public async Task Invalid_finish_reason_still_reports_every_returned_tool_proposal()
    {
        var model = new RecordingModel(_ => new("Invalid completed reply with tools.",
            [Call(), Call("another-tool") with { CallId = "call-2" }], ModelFinishReason.Completed));
        var sink = new RecordingSink();
        var executor = new RecordingExecutor();
        var result = await new AgentRunner(model, new TestToolProvider(), executor, new(), sink).RunAsync(new("input"));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(new[] { "call-1", "call-2" }, sink.Events
            .Where(e => e.EventType == ExecutionEventType.ToolCallProposed)
            .Select(e => e.Payload.GetProperty("callId").GetString()));
        Assert.Empty(executor.Calls);
        Assert.Equal(ExecutionEventType.RunFailed, sink.Events.Last().EventType);
    }

    private sealed class TokenRecordingSink : IExecutionEventSink
    {
        public List<(ExecutionEvent Event, CancellationToken Token)> Attempts { get; } = [];
        public ValueTask PublishAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken)
        {
            Attempts.Add((executionEvent, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceSink : IExecutionEventSink
    {
        public List<long> Attempted { get; } = [];
        public List<long> Delivered { get; } = [];
        public ValueTask PublishAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken)
        {
            Attempted.Add(executionEvent.Sequence);
            if (executionEvent.Sequence == 2) throw new InvalidOperationException("Single delivery failure.");
            Delivered.Add(executionEvent.Sequence);
            return ValueTask.CompletedTask;
        }
    }

    private static bool IsTerminal(ExecutionEventType type) => type is
        ExecutionEventType.RunCompleted or ExecutionEventType.RunFailed or
        ExecutionEventType.RunCancelled or ExecutionEventType.RunLimitReached;

    private sealed class RecordingSink : IExecutionEventSink
    {
        public ConcurrentQueue<ExecutionEvent> Events { get; } = new();
        public async ValueTask PublishAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken)
        {
            await Task.Yield(); // Exercise awaiting an asynchronous observer rather than only synchronous sinks.
            cancellationToken.ThrowIfCancellationRequested();
            Events.Enqueue(executionEvent);
        }
    }

    private sealed class ThrowingSink(bool cancelException) : IExecutionEventSink
    {
        public int Attempts { get; private set; }
        public async ValueTask PublishAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Attempts++;
            if (cancelException) throw new OperationCanceledException("Observer cancelled itself.");
            throw new InvalidOperationException("Observer failed.");
        }
    }

    private sealed class FinalModel : IModelProvider
    {
        public Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelReply("Done.", [], ModelFinishReason.Completed));
    }
}
