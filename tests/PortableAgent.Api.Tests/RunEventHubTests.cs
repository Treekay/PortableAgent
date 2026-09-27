using System.Text.Json;
using PortableAgent.Api.Streaming;
using PortableAgent.Core.Execution.Events;

namespace PortableAgent.Api.Tests;

public sealed class RunEventHubTests
{
    internal static ExecutionEvent Event(Guid id, long seq, ExecutionEventType type = ExecutionEventType.ModelTurnStarted) =>
        new(Guid.NewGuid(), id, seq, DateTimeOffset.UtcNow, type, JsonSerializer.SerializeToElement(new { modelTurn = 1 }));

    [Fact]
    public async Task Broadcast_is_independent_routed_and_removal_is_idempotent()
    {
        using var hub = new RunEventHub();
        var id = Guid.NewGuid();
        await using var first = hub.Subscribe(id);
        await using var second = hub.Subscribe(id);
        await using var other = hub.Subscribe(Guid.NewGuid());
        await hub.PublishAsync(Event(id, 1), default);
        Assert.Equal(1, await first.Notifications.ReadAsync());
        Assert.Equal(1, await second.Notifications.ReadAsync());
        Assert.False(other.Notifications.TryRead(out _));
        await first.DisposeAsync();
        await first.DisposeAsync();
        await hub.PublishAsync(Event(id, 2), default);
        Assert.False(first.Notifications.TryRead(out _));
        Assert.Equal(2, await second.Notifications.ReadAsync());
        Assert.Equal(2, hub.SubscriberCount);
    }

    [Fact]
    public async Task Overflow_cancels_only_slow_subscription_without_waiting_for_callbacks()
    {
        using var hub = new RunEventHub(1);
        var id = Guid.NewGuid();
        await using var slow = hub.Subscribe(id);
        await using var fast = hub.Subscribe(id);
        using var callbackRelease = new ManualResetEventSlim();
        using var callback = slow.Terminated.Register(() => callbackRelease.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await hub.PublishAsync(Event(id, 1), default);
            Assert.Equal(1, await fast.Notifications.ReadAsync());
            var publish = hub.PublishAsync(Event(id, 2), default);
            Assert.True(publish.IsCompletedSuccessfully);
            Assert.True(slow.Overflowed);
            Assert.True(slow.Terminated.IsCancellationRequested);
            Assert.False(fast.Terminated.IsCancellationRequested);
            Assert.Equal(2, await fast.Notifications.ReadAsync());
            Assert.Equal(1, hub.SubscriberCount);
            Assert.Equal(1, await slow.Notifications.ReadAsync());
            Assert.False(await slow.Notifications.WaitToReadAsync());
        }
        finally { callbackRelease.Set(); }
    }

    [Fact]
    public async Task Concurrent_publish_subscribe_dispose_and_stop_leave_no_dead_entries()
    {
        using var hub = new RunEventHub(2);
        var id = Guid.NewGuid();
        Assert.True(hub.PublishAsync(Event(id, 1), default).IsCompletedSuccessfully);
        await Task.WhenAll(Enumerable.Range(0, 30).Select(async worker =>
        {
            await Task.Yield();
            for (var i = 0; i < 10; i++)
            {
                await using var subscription = hub.Subscribe(id);
                await hub.PublishAsync(Event(id, worker * 10 + i + 1), default);
                subscription.Notifications.TryRead(out _);
            }
        }));
        Assert.Equal(0, hub.SubscriberCount);
        await using var live = hub.Subscribe(id);
        hub.Stop();
        Assert.True(live.Terminated.IsCancellationRequested);
        Assert.Equal(0, hub.SubscriberCount);
        Assert.True(hub.PublishAsync(Event(id, 500), default).IsCompletedSuccessfully);
        Assert.Throws<ObjectDisposedException>(() => hub.Subscribe(id));
    }
}
