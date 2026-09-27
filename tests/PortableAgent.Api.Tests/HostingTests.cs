using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PortableAgent.Api.Hosting;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;

namespace PortableAgent.Api.Tests;

public sealed class HostingTests
{
    [Fact]
    public async Task Registry_enforces_unique_ordinal_identities_and_disposes_all_resources_once()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime();
        var a = runtime.Register(db.Store);
        var duplicateAgent = new AgentRuntimeRegistration(a.AgentId, "Duplicate", "different-v1", a.Runner);
        var duplicateDefinition = new AgentRuntimeRegistration("different", "Duplicate", a.RuntimeDefinitionId, a.Runner);
        Assert.Throws<ArgumentException>(() => new AgentRuntimeRegistry([a, duplicateAgent]));
        Assert.Throws<ArgumentException>(() => new AgentRuntimeRegistry([a, duplicateDefinition]));
        var b = new AgentRuntimeRegistration("TEST", "Upper case", "TEST-v1", a.Runner);
        var registry = new AgentRuntimeRegistry([a, b]);
        Assert.Same(a, registry.ByAgent("test"));
        Assert.Same(b, registry.ByDefinition("TEST-v1"));
        Assert.Null(registry.ByAgent("Test"));
        await registry.DisposeAsync();
        await registry.DisposeAsync();
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public async Task Failed_CAS_never_acknowledges_and_duplicate_id_cannot_overwrite_durable_run()
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db, new TestRuntime { Approval = true });
        var id = await host.StartRunAsync();
        await host.WaitAsync(id, "AwaitingApproval");
        var saved = (await db.Store.GetAsync(id, default))!;
        var eventsBefore = await db.Store.ReadEventsAfterAsync(id, 0, 100, default);
        var signals = new RunOperationSignals();
        var signal = signals.Arm(id, RunOperationKind.Approval);
        var store = new AcknowledgingRunStateStore(db.Store, signals);
        var replacement = saved.Snapshot();
        replacement.Lifecycle = RunLifecycleState.Running;
        replacement.Version += 101;
        replacement.LastSequence += 2;
        ExecutionEvent Event(long sequence, ExecutionEventType type) =>
            new(Guid.NewGuid(), id, sequence, DateTimeOffset.UtcNow, type, JsonSerializer.SerializeToElement(new { }));
        Assert.False(await store.TryReplaceAsync(id, saved.Version + 100, replacement,
            [Event(saved.LastSequence + 1, ExecutionEventType.ApprovalResolved), Event(saved.LastSequence + 2, ExecutionEventType.RunResumed)], default));
        Assert.False(signal.Committed.IsCompleted);
        var runtime = new TestRuntime();
        var result = await runtime.Register(store).Runner.RunAsync(id, new("must not replace"));
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.False(runtime.ModelEntered.Task.IsCompleted);
        Assert.Empty(runtime.Calls);
        Assert.Equal(saved.Version, (await db.Store.GetAsync(id, default))!.Version);
        Assert.Equal(eventsBefore.Select(e => e.EventId), (await db.Store.ReadEventsAfterAsync(id, 0, 100, default)).Select(e => e.EventId));
    }

    [Fact]
    public async Task Cancelling_resumed_execution_preserves_consumed_approval()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { Approval = true, BlockTool = true };
        await using var host = await ApiTestHost.StartAsync(db, runtime);
        var id = await host.StartRunAsync();
        var pending = await host.WaitAsync(id, "AwaitingApproval");
        Assert.Equal(HttpStatusCode.Accepted, (await host.ApproveAsync(id, pending.PendingApproval!.ApprovalId)).StatusCode);
        await runtime.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Accepted, (await host.Client.PostAsync($"/api/runs/{id}/cancel", null)).StatusCode);
        await host.WaitAsync(id, "Cancelled");
        Assert.Single((await db.Store.GetAsync(id, default))!.ResolvedApprovals);
        Assert.Single(runtime.Calls);
        Assert.Equal(HttpStatusCode.Conflict, (await host.ApproveAsync(id, pending.PendingApproval.ApprovalId)).StatusCode);
    }

    [Fact]
    public async Task Cancel_completion_race_keeps_runtime_terminal_snapshot()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { BlockTool = true };
        await using var host = await ApiTestHost.StartAsync(db, runtime);
        var id = await host.StartRunAsync();
        await runtime.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancel = host.Client.PostAsync($"/api/runs/{id}/cancel", null);
        runtime.ToolRelease.TrySetResult();
        Assert.Contains((await cancel).StatusCode, new[] { HttpStatusCode.Accepted, HttpStatusCode.Conflict });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (host.Coordinator.IsActive(id)) await Task.Delay(10, timeout.Token);
        var state = await host.GetAsync(id);
        Assert.Contains(state.Status, new[] { "Completed", "Cancelled" });
        Assert.Equal(state.Status, (await db.Store.GetAsync(id, default))!.Lifecycle.ToString());
        var events = await db.Store.ReadEventsAfterAsync(id, 0, 100, default);
        Assert.Single(events, e => e.EventType is ExecutionEventType.RunCompleted or ExecutionEventType.RunCancelled);
    }

    [Fact]
    public async Task Stopping_coordinator_returns_HTTP_503_without_creating_a_run()
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db, new TestRuntime());
        await host.Coordinator.StopAsync(default);
        var response = await host.Client.PostAsJsonAsync("/api/runs", new { agentId = "test", message = "execute" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("host_stopping", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, await db.CountAsync());
    }
}
