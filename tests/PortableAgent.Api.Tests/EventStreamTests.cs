using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PortableAgent.Api.Streaming;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;

namespace PortableAgent.Api.Tests;

public sealed class EventStreamTests
{
    internal static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task Completed_history_has_exact_wire_DTO_redaction_order_and_terminal_EOF()
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db, new TestRuntime());
        var id = await host.StartRunAsync("SECRET user prompt");
        var done = await host.WaitAsync(id, "Completed");
        using var stream = await SseClient.OpenAsync(host.Client, id, "?afterSequence=0");
        await stream.ReadToEndAsync();
        var stored = await db.Store.ReadEventsAfterAsync(id, 0, 256, default);
        Assert.Equal(stored.Select(e => e.EventId), stream.Events.Select(e => e.EventId));
        Assert.All(stream.Frames, f => { Assert.Equal(f.Data!.Sequence, f.Id); Assert.Equal(f.Data.EventType, f.Event); });
        for (var i = 0; i < stored.Count; i++)
        {
            Assert.Equal(stored[i].RunId, stream.Events[i].RunId);
            Assert.Equal(stored[i].OccurredAt, stream.Events[i].OccurredAt);
            Assert.Equal(stored[i].EventType.ToString(), stream.Events[i].EventType);
            Assert.True(JsonElement.DeepEquals(stored[i].Payload, stream.Events[i].Payload));
        }
        var body = JsonSerializer.Serialize(stream.Events);
        Assert.DoesNotContain("SECRET", body);
        Assert.DoesNotContain("frozen", body);
        Assert.DoesNotContain("Finished.", body);
        Assert.Equal("RunCompleted", stream.Events[^1].EventType);
        Assert.Equal(done.SnapshotSequence, stream.Events[^1].Sequence);
        await WaitForAsync(() => host.Hub.SubscriberCount == 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Consumed_or_future_terminal_cursor_returns_204(bool header, bool future)
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db, new TestRuntime());
        var id = await host.StartRunAsync();
        var terminal = await host.WaitAsync(id, "Completed");
        var cursor = future ? long.MaxValue : terminal.SnapshotSequence;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/runs/{id}/events" + (header ? "" : $"?afterSequence={cursor}"));
        if (header) request.Headers.Add("Last-Event-ID", cursor.ToString());
        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Hub.SubscriberCount);
    }

    [Theory]
    [InlineData("5", "2", 5)]
    [InlineData("5", "invalid", 5)]
    [InlineData("", "2", 2)]
    public async Task Nonempty_header_takes_precedence_without_resetting_cursor(string header, string query, long expected)
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db, new TestRuntime());
        var id = await host.StartRunAsync();
        await host.WaitAsync(id, "Completed");
        using var stream = await SseClient.OpenAsync(host.Client, id, $"?afterSequence={query}", header);
        await stream.ReadToEndAsync();
        var stored = await db.Store.ReadEventsAfterAsync(id, expected, 256, default);
        Assert.Equal(stored.Select(e => e.Sequence), stream.Events.Select(e => e.Sequence));
    }

    [Theory]
    [InlineData("-1", false)]
    [InlineData("1.5", false)]
    [InlineData("9223372036854775808", false)]
    [InlineData("", false)]
    [InlineData("garbage", true)]
    [InlineData("-1", true)]
    [InlineData("9223372036854775808", true)]
    public async Task Invalid_cursor_is_400_before_any_subscription(string cursor, bool header)
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/runs/{Guid.NewGuid()}/events?afterSequence=" + (header ? "0" : cursor));
        if (header) request.Headers.TryAddWithoutValidation("Last-Event-ID", cursor);
        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, host.Hub.SubscriberCount);
    }

    [Fact]
    public async Task Unknown_run_is_404_before_headers_start()
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db);
        var response = await host.Client.GetAsync($"/api/runs/{Guid.NewGuid()}/events");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("run_not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, host.Hub.SubscriberCount);
    }

    [Fact]
    public async Task Two_live_clients_receive_same_history_and_disconnect_does_not_cancel_run()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { BlockModel = true, BlockTool = true };
        await using var host = await ApiTestHost.StartAsync(db, runtime);
        var id = await host.StartRunAsync();
        await runtime.ModelEntered.Task;
        using var first = await SseClient.OpenAsync(host.Client, id);
        using var second = await SseClient.OpenAsync(host.Client, id);
        await Task.WhenAll(first.UntilAsync("ModelTurnStarted"), second.UntilAsync("ModelTurnStarted"));
        Assert.Equal(first.Events.Select(e => e.EventId), second.Events.Select(e => e.EventId));
        first.Dispose();
        await WaitForAsync(() => host.Hub.SubscriberCount == 1);
        Assert.True(host.Coordinator.IsActive(id));
        runtime.ModelRelease.TrySetResult();
        await second.UntilAsync("ToolExecutionStarted");
        Assert.False(runtime.ToolRelease.Task.IsCompleted);
        runtime.ToolRelease.TrySetResult();
        await second.ReadToEndAsync();
        var done = await host.WaitAsync(id, "Completed");
        Assert.Equal("Finished.", done.FinalText);
        Assert.Equal((await db.Store.ReadEventsAfterAsync(id, 0, 256, default)).Select(e => e.EventId), second.Events.Select(e => e.EventId));
        // Reconnect the disconnected client from its last received id, without duplicates.
        using var reconnected = await SseClient.OpenAsync(host.Client, id, lastEventId: first.Events[^1].Sequence.ToString());
        await reconnected.ReadToEndAsync();
        Assert.Equal(second.Events.Select(e => e.EventId), first.Events.Concat(reconnected.Events).Select(e => e.EventId));
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task ApprovalRequired_remains_open_and_same_connection_follows_resume(string decision)
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db, new TestRuntime { Approval = true });
        var id = await host.StartRunAsync();
        using var stream = await SseClient.OpenAsync(host.Client, id);
        await stream.UntilAsync("ApprovalRequired");
        var paused = await host.WaitAsync(id, "AwaitingApproval");
        Assert.Equal(1, host.Hub.SubscriberCount);
        Assert.Equal(HttpStatusCode.Accepted, (await host.ApproveAsync(id, paused.PendingApproval!.ApprovalId, decision)).StatusCode);
        await stream.ReadToEndAsync();
        Assert.Single(stream.Events, e => e.EventType == "RunStarted");
        Assert.Contains(stream.Events, e => e.EventType == "ApprovalResolved");
        Assert.Contains(stream.Events, e => e.EventType == "RunResumed");
        Assert.Equal("RunCompleted", stream.Events[^1].EventType);
        Assert.Equal(stream.Events.Select(e => e.Sequence).Order(), stream.Events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task Event_committed_during_replay_is_not_lost_or_duplicated()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { BlockModel = true };
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var control = new StoreControl { AfterRead = async (_, _, ct) =>
        {
            if (Interlocked.Increment(ref reads) != 1) return;
            readEntered.TrySetResult();
            await releaseRead.Task.WaitAsync(ct);
        }};
        await using var host = await ApiTestHost.StartAsync(db, runtime, control);
        var id = await host.StartRunAsync();
        await runtime.ModelEntered.Task;
        var opening = SseClient.OpenAsync(host.Client, id);
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, host.Hub.SubscriberCount); // Actual endpoint subscribed before this read.
        runtime.ModelRelease.TrySetResult();
        await host.WaitAsync(id, "Completed"); // New events persisted while old page is withheld.
        releaseRead.TrySetResult();
        using var stream = await opening;
        await stream.ReadToEndAsync();
        Assert.Equal((await db.Store.ReadEventsAfterAsync(id, 0, 256, default)).Select(e => e.EventId), stream.Events.Select(e => e.EventId));
        Assert.Equal(stream.Events.Length, stream.Events.Select(e => e.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task Poll_recovers_missing_final_notification_even_when_live_sink_throws()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { BlockModel = true };
        await using var host = await ApiTestHost.StartAsync(db, runtime, configure: b =>
            b.Services.AddSingleton(new RunEventStreamSettings { PollInterval = TimeSpan.FromMilliseconds(50) }), sink: new FailingSink());
        var id = await host.StartRunAsync();
        using var stream = await SseClient.OpenAsync(host.Client, id);
        await stream.UntilAsync("ModelTurnStarted");
        var comment = await stream.ReadAsync();
        Assert.Equal("keepalive", comment!.Comment);
        Assert.Null(comment.Id);
        runtime.ModelRelease.TrySetResult();
        await stream.ReadToEndAsync();
        await host.WaitAsync(id, "Completed");
        Assert.Equal((await db.Store.ReadEventsAfterAsync(id, 0, 256, default)).Select(e => e.EventId), stream.Events.Select(e => e.EventId));
        Assert.Equal("RunCompleted", stream.Events[^1].EventType);
        Assert.Null(stream.Frames[^1].Comment);
    }

    [Fact]
    public async Task Duplicate_reordered_and_unpersisted_notifications_never_become_events()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { BlockModel = true };
        await using var host = await ApiTestHost.StartAsync(db, runtime);
        var id = await host.StartRunAsync();
        using var stream = await SseClient.OpenAsync(host.Client, id);
        await stream.UntilAsync("ModelTurnStarted");
        foreach (var seq in new long[] { 999, 2, 999, 1, 4 })
            await host.Hub.PublishAsync(RunEventHubTests.Event(id, seq), default);
        runtime.ModelRelease.TrySetResult();
        await stream.ReadToEndAsync();
        Assert.Equal((await db.Store.ReadEventsAfterAsync(id, 0, 256, default)).Select(e => e.EventId), stream.Events.Select(e => e.EventId));
        Assert.DoesNotContain(stream.Events, e => e.Sequence == 999);
    }

    [Fact]
    public async Task Pages_preserve_sequence_gaps_and_terminal_cursor_uses_only_terminal_snapshot()
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db);
        var id = Guid.NewGuid();
        var events = Enumerable.Range(1, 300).Select(n => RunEventHubTests.Event(id, n * 2L,
            n == 300 ? ExecutionEventType.RunCompleted : ExecutionEventType.ModelTurnStarted)).ToArray();
        await db.Store.CreateAsync(new RunState { RunId = id, RuntimeDefinitionId = "test", Limits = new(),
            Lifecycle = RunLifecycleState.Completed, LastSequence = 600 }, events, default);
        using var stream = await SseClient.OpenAsync(host.Client, id);
        await stream.ReadToEndAsync();
        Assert.Equal(events.Select(e => e.Sequence), stream.Events.Select(e => e.Sequence));
        Assert.Equal(300, stream.Events.Length);
    }

    [Fact]
    public async Task Orphan_Running_replays_beyond_snapshot_then_heartbeats_and_future_cursor_is_not_reset()
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db, configure: b =>
            b.Services.AddSingleton(new RunEventStreamSettings { PollInterval = TimeSpan.FromMilliseconds(50) }));
        var id = Guid.NewGuid();
        await db.Store.CreateAsync(new RunState { RunId = id, RuntimeDefinitionId = "old", Limits = new(), LastSequence = 1 },
            [RunEventHubTests.Event(id, 1, ExecutionEventType.RunStarted)], default);
        await db.Store.AppendEventAsync(RunEventHubTests.Event(id, 3), default);
        using (var stream = await SseClient.OpenAsync(host.Client, id))
        {
            await stream.UntilAsync("ModelTurnStarted");
            Assert.Equal(new long[] { 1, 3 }, stream.Events.Select(e => e.Sequence));
            Assert.Equal("keepalive", (await stream.ReadAsync())!.Comment);
        }
        using (var future = await SseClient.OpenAsync(host.Client, id, "?afterSequence=999"))
        {
            Assert.Equal("keepalive", (await future.ReadAsync())!.Comment);
            Assert.Empty(future.Events);
        }
        Assert.Equal(RunLifecycleState.Running, (await db.Store.GetAsync(id, default))!.Lifecycle);
        Assert.False(host.Coordinator.IsActive(id));
    }

    [Theory]
    [InlineData(RunLifecycleState.Failed, ExecutionEventType.RunFailed)]
    [InlineData(RunLifecycleState.Cancelled, ExecutionEventType.RunCancelled)]
    [InlineData(RunLifecycleState.LimitReached, ExecutionEventType.RunLimitReached)]
    public async Task Every_terminal_kind_closes_after_flush(RunLifecycleState lifecycle, ExecutionEventType type)
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db);
        var id = Guid.NewGuid();
        await db.Store.CreateAsync(new RunState { RunId = id, RuntimeDefinitionId = "test", Limits = new(), Lifecycle = lifecycle, LastSequence = 1 },
            [RunEventHubTests.Event(id, 1, type)], default);
        using var stream = await SseClient.OpenAsync(host.Client, id);
        await stream.ReadToEndAsync();
        Assert.Equal(type.ToString(), Assert.Single(stream.Events).EventType);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.GetAsync($"/api/runs/{id}/events?afterSequence=1")).StatusCode);
    }

    private sealed class FailingSink : IExecutionEventSink
    {
        public ValueTask PublishAsync(ExecutionEvent e, CancellationToken ct) => throw new IOException("Injected missing notification");
    }
}
