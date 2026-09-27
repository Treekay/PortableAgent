using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Core.Models;
using PortableAgent.Infrastructure.Models;
using PortableAgent.Persistence.Sqlite;

namespace PortableAgent.Adapters.Mcp.Tests;

public sealed class RuntimeIntegrationTests
{
    private static AgentRunner Runner(McpToolAdapter adapter, RecordingModel model, Events events, IRunStateStore? store = null) =>
        new("sample-mcp-demo-v1", model, adapter, adapter, new(), events, runStore: store);

    [Fact]
    public async Task Unchanged_runtime_completes_real_sample_loop_with_sqlite_events_and_owned_snapshot()
    {
        await using var server = await TestServerFixture.StartAsync(sampleTools: true);
        var path = Path.Combine(Path.GetTempPath(), $"portable-mcp-{Guid.NewGuid():N}.db");
        try
        {
            await SqliteRunStateStore.InitializeAsync(path);
            AgentRunResult result;
            var events = new Events();
            var model = new RecordingModel();
            await using (var adapter = new McpToolAdapter(new("sample-mcp", server.Endpoint)))
                result = await Runner(adapter, model, events, new SqliteRunStateStore(path)).RunAsync(new("Check the sample service status."));
            Assert.Equal(RunStatus.Completed, result.Status);
            Assert.Equal("The sample service is online.", result.FinalText);
            Assert.Equal(2, model.Requests.Count);
            Assert.Single(server.Methods, m => m == "tools/call");
            var fresh = new SqliteRunStateStore(path);
            var state = (await fresh.GetAsync(result.RunId, default))!;
            Assert.Equal(RunLifecycleState.Completed, state.Lifecycle);
            Assert.Equal("sample-mcp-demo-v1", state.RuntimeDefinitionId);
            Assert.Equal(2, state.ModelTurns);
            Assert.Equal(1, state.ToolCalls);
            Assert.Equal("sample-mcp", Assert.Single(state.ToolCatalog).Id.SourceId);
            Assert.Equal("ok", state.Conversation[2].ToolResult!.Output!.Value.GetProperty("status").GetString());
            var persisted = await fresh.ReadEventsAfterAsync(result.RunId, 0, 100, default);
            Assert.Equal(events.Items.Select(e => e.EventId), persisted.Select(e => e.EventId));
            Assert.Equal(Enumerable.Range(1, persisted.Count).Select(i => (long)i), persisted.Select(e => e.Sequence));
            Assert.All(persisted, e => Assert.Equal(result.RunId, e.RunId));
            Assert.Equal(state.LastSequence, persisted[^1].Sequence);
            Assert.Equal(new[]
            {
                ExecutionEventType.RunStarted, ExecutionEventType.ToolDiscoveryStarted, ExecutionEventType.ToolDiscoveryCompleted,
                ExecutionEventType.ModelTurnStarted, ExecutionEventType.ModelTurnCompleted, ExecutionEventType.ToolCallProposed,
                ExecutionEventType.PolicyEvaluationStarted, ExecutionEventType.PolicyEvaluationCompleted,
                ExecutionEventType.ToolExecutionStarted, ExecutionEventType.ToolExecutionCompleted,
                ExecutionEventType.ModelTurnStarted, ExecutionEventType.ModelTurnCompleted, ExecutionEventType.RunCompleted
            }, persisted.Select(e => e.EventType));
            Assert.DoesNotContain(persisted, e => e.Payload.GetRawText().Contains("Check the sample service status", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task Mcp_tool_error_terminates_runtime_without_model_retry()
    {
        await using var server = await TestServerFixture.StartAsync();
        server.Handler = (_, _) => ValueTask.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "Service check failed." }] });
        await using var adapter = new McpToolAdapter(new("sample-mcp", server.Endpoint));
        var model = new RecordingModel();
        var events = new Events();
        var result = await Runner(adapter, model, events).RunAsync(new("Check the sample service status."));
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal("Service check failed.", result.Error);
        Assert.Single(model.Requests);
        Assert.Single(server.Calls);
        Assert.False(Assert.Single(events.Items, e => e.EventType == ExecutionEventType.ToolExecutionCompleted).Payload.GetProperty("success").GetBoolean());
        Assert.Equal(ExecutionEventType.RunFailed, events.Items[^1].EventType);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("http")]
    [InlineData("malformed")]
    public async Task Infrastructure_failures_throw_in_adapter_and_fail_run_without_fake_result_or_retry(string fault)
    {
        await using var server = await TestServerFixture.StartAsync();
        if (fault == "protocol") server.Handler = (_, _) => throw new McpProtocolException("Unknown remote tool.", McpErrorCode.InvalidParams);
        else server.Fault = fault;
        await using var adapter = new McpToolAdapter(new("sample-mcp", server.Endpoint));
        var model = new RecordingModel();
        var events = new Events();
        var result = await Runner(adapter, model, events).RunAsync(new("Check the sample service status."));
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Single(model.Requests);
        Assert.Single(server.Methods, m => m == "tools/call");
        Assert.Single(events.Items, e => e.EventType == ExecutionEventType.ToolExecutionStarted);
        Assert.DoesNotContain(events.Items, e => e.EventType == ExecutionEventType.ToolExecutionCompleted);
        Assert.Equal(ExecutionEventType.RunFailed, events.Items[^1].EventType);
    }

    [Fact]
    public async Task Unavailable_server_is_lazy_initialization_failure_observed_by_runtime()
    {
        var server = await TestServerFixture.StartAsync();
        var endpoint = server.Endpoint;
        await server.DisposeAsync();
        await using var adapter = new McpToolAdapter(new("sample-mcp", endpoint)); // construction performs no I/O
        var model = new RecordingModel();
        var events = new Events();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await Runner(adapter, model, events).RunAsync(new("Check the sample service status."), timeout.Token);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Empty(model.Requests);
        Assert.DoesNotContain(events.Items, e => e.EventType == ExecutionEventType.ToolExecutionStarted);
        Assert.Equal(ExecutionEventType.RunFailed, events.Items[^1].EventType);
    }

    [Fact]
    public async Task Cancellation_reaches_remote_handler_and_runtime_without_retry()
    {
        await using var server = await TestServerFixture.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Handler = async (_, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return new();
        };
        await using var adapter = new McpToolAdapter(new("sample-mcp", server.Endpoint));
        using var source = new CancellationTokenSource();
        var events = new Events();
        var execution = Runner(adapter, new(), events).RunAsync(new("Check the sample service status."), source.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Single(server.Calls);
        Assert.DoesNotContain(events.Items, e => e.EventType == ExecutionEventType.ToolExecutionCompleted);
        Assert.Equal(ExecutionEventType.RunCancelled, events.Items[^1].EventType);
    }

    private sealed class RecordingModel : IModelProvider
    {
        private readonly ServerStatusScriptedModelProvider _inner = new();
        public List<ModelRequest> Requests { get; } = [];
        public Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken ct)
        { Requests.Add(request); return _inner.GenerateAsync(request, ct); }
    }
    private sealed class Events : IExecutionEventSink
    {
        public List<ExecutionEvent> Items { get; } = [];
        public ValueTask PublishAsync(ExecutionEvent e, CancellationToken ct)
        { Items.Add(e); return ValueTask.CompletedTask; }
    }
}
