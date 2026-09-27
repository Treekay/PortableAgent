using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PortableAgent.Api.Streaming;

namespace PortableAgent.Api.Tests;

public sealed class StreamFailureTests
{
    [Fact]
    public async Task Overflow_interrupts_blocked_response_write_and_reconnect_recovers_durable_gap()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { BlockModel = true };
        await using var host = await ApiTestHost.StartAsync(db, runtime, configure: b => b.Services.AddSingleton(new RunEventHub(2)));
        var id = await host.StartRunAsync();
        await runtime.ModelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var context = new DefaultHttpContext();
        using var body = new ControlledBody(block: true);
        context.Response.Body = body;
        var streaming = new RunEventStream(db.Store, host.Hub, new(),
            host.App.Services.GetRequiredService<IHostApplicationLifetime>(), NullLogger<RunEventStream>.Instance);
        var slow = streaming.WriteAsync(context, id, 0);
        await body.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The first event was flushed, the second write is deliberately held by the response stream.
        Assert.Contains("id: 1\n", Encoding.UTF8.GetString(body.Written.ToArray()));
        await using (var fast = host.Hub.Subscribe(id))
        {
            for (var seq = 1; seq <= 3; seq++)
            {
                await host.Hub.PublishAsync(RunEventHubTests.Event(id, seq), default);
                Assert.Equal(seq, await fast.Notifications.ReadAsync());
            }
            await slow.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(body.WriteCancelled);
            Assert.False(fast.Terminated.IsCancellationRequested);
            Assert.Equal(1, host.Hub.SubscriberCount);
            Assert.True(host.Coordinator.IsActive(id));
        }
        runtime.ModelRelease.TrySetResult();
        await host.WaitAsync(id, "Completed");
        Assert.Single(runtime.Calls);
        using var reconnected = await SseClient.OpenAsync(host.Client, id, lastEventId: "1");
        await reconnected.ReadToEndAsync();
        var stored = await db.Store.ReadEventsAfterAsync(id, 0, 256, default);
        Assert.Equal(stored.Skip(1).Select(e => e.EventId), reconnected.Events.Select(e => e.EventId));
        Assert.Equal("RunCompleted", reconnected.Events[^1].EventType);
        await EventStreamTests.WaitForAsync(() => host.Hub.SubscriberCount == 0);
    }

    [Fact]
    public async Task Database_failure_after_headers_ends_connection_without_ProblemDetails_or_dead_subscriber()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { BlockModel = true };
        var failReads = 0;
        var control = new StoreControl { AfterRead = (_, _, _) =>
        {
            if (Volatile.Read(ref failReads) == 1) throw new IOException("SECRET replay failure");
            return Task.CompletedTask;
        }};
        await using var host = await ApiTestHost.StartAsync(db, runtime, control);
        var id = await host.StartRunAsync();
        using var stream = await SseClient.OpenAsync(host.Client, id);
        await stream.UntilAsync("ModelTurnStarted");
        Volatile.Write(ref failReads, 1);
        await host.Hub.PublishAsync(RunEventHubTests.Event(id, 99), default);
        await Assert.ThrowsAnyAsync<IOException>(() => stream.ReadAsync());
        await EventStreamTests.WaitForAsync(() => host.Hub.SubscriberCount == 0);
        Assert.True(host.Coordinator.IsActive(id));
        runtime.ModelRelease.TrySetResult();
        await host.WaitAsync(id, "Completed");
    }

    [Fact]
    public async Task Response_failure_disposes_subscription_and_does_not_cancel_execution()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { BlockModel = true };
        await using var host = await ApiTestHost.StartAsync(db, runtime);
        var id = await host.StartRunAsync();
        await runtime.ModelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponse());
        using var body = new ControlledBody(block: false);
        context.Response.Body = body;
        await host.App.Services.GetRequiredService<RunEventStream>().WriteAsync(context, id, 0);
        Assert.Equal(0, host.Hub.SubscriberCount);
        Assert.True(host.Coordinator.IsActive(id));
        Assert.DoesNotContain("internal_error", Encoding.UTF8.GetString(body.Written.ToArray()));
        runtime.ModelRelease.TrySetResult();
        await host.WaitAsync(id, "Completed");
    }

    [Fact]
    public async Task Host_shutdown_terminates_subscriptions()
    {
        using var db = new DatabaseFile();
        var host = await ApiTestHost.StartAsync(db, new TestRuntime { BlockModel = true });
        var id = await host.StartRunAsync();
        using var stream = await SseClient.OpenAsync(host.Client, id);
        await stream.UntilAsync("ModelTurnStarted");
        var hub = host.Hub;
        host.App.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await EventStreamTests.WaitForAsync(() => hub.SubscriberCount == 0);
        await host.DisposeAsync();
    }

    private sealed class StartedResponse : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class ControlledBody(bool block) : Stream
    {
        public MemoryStream Written { get; } = new();
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WriteCancelled { get; private set; }
        private int _writes;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _writes) == 2)
            {
                Blocked.TrySetResult();
                if (!block) throw new IOException("Injected disconnected response");
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { WriteCancelled = true; throw; }
            }
            await Written.WriteAsync(buffer, ct);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) Written.Dispose(); base.Dispose(disposing); }
    }
}
