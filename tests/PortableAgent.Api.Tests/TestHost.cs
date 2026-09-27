using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PortableAgent.Api.Contracts;
using PortableAgent.Api.Hosting;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Core.Models;
using PortableAgent.Core.Policies;
using PortableAgent.Core.Tools;
using PortableAgent.Infrastructure.Policies;
using PortableAgent.Persistence.Sqlite;

namespace PortableAgent.Api.Tests;

internal sealed class DatabaseFile : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"portable-api-{Guid.NewGuid():N}.db");
    public SqliteRunStateStore Store => new(Path);
    public async Task<long> CountAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Runs";
        return (long)(await command.ExecuteScalarAsync())!;
    }
    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(Path + suffix);
    }
}

internal sealed class ApiTestHost(WebApplication app, HttpClient client) : IAsyncDisposable
{
    public WebApplication App { get; } = app;
    public HttpClient Client { get; } = client;
    public RunExecutionCoordinator Coordinator => App.Services.GetRequiredService<RunExecutionCoordinator>();
    public static async Task<ApiTestHost> StartAsync(DatabaseFile db, TestRuntime? runtime = null, StoreControl? control = null,
        string? flightEndpoint = null)
    {
        var args = new List<string> { "--DatabasePath", db.Path };
        if (flightEndpoint is not null) args.AddRange(["--Agents:flight:Endpoint", flightEndpoint]);
        var app = ApiApplication.Build(args.ToArray(), builder =>
        {
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            if (control is not null)
            {
                builder.Services.RemoveAll<IRunStateStore>();
                builder.Services.AddSingleton<IRunStateStore>(sp => new AcknowledgingRunStateStore(
                    new ControlledStore(sp.GetRequiredService<SqliteRunStateStore>(), control), sp.GetRequiredService<RunOperationSignals>()));
            }
            if (runtime is not null)
            {
                builder.Services.RemoveAll<AgentRuntimeRegistry>();
                builder.Services.AddSingleton(sp => new AgentRuntimeRegistry([runtime.Register(sp.GetRequiredService<IRunStateStore>())]));
            }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app, new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) });
    }

    public async Task<Guid> StartRunAsync(string message = "execute", string agent = "test")
    {
        var response = await Client.PostAsJsonAsync("/api/runs", new { agentId = agent, message });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<RunAccepted>())!;
        Assert.Equal("accepted", accepted.Status);
        Assert.Equal($"/api/runs/{accepted.RunId}", response.Headers.Location!.OriginalString);
        return accepted.RunId;
    }
    public async Task<RunDto> GetAsync(Guid id) => (await Client.GetFromJsonAsync<RunDto>($"/api/runs/{id}"))!;
    public async Task<RunDto> WaitAsync(Guid id, string status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var run = await GetAsync(id);
            if (run.Status == status && !run.IsActive) return run;
            await Task.Delay(15, timeout.Token);
        }
    }
    public Task<HttpResponseMessage> ApproveAsync(Guid id, Guid approval, string decision = "approve", CancellationToken ct = default) =>
        Client.PostAsJsonAsync($"/api/runs/{id}/approvals/{approval}", new { decision }, ct);
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

internal sealed class TestRuntime : IModelProvider, IToolProvider, IToolExecutor, IAsyncDisposable
{
    public string DefinitionId { get; init; } = "test-v1";
    public bool Approval { get; init; }
    public bool BlockModel { get; init; }
    public bool BlockTool { get; init; }
    public bool FailModel { get; init; }
    public TaskCompletionSource ModelEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ModelRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ToolEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ToolRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<ToolCall> Calls { get; } = new();
    public int DisposeCount;
    public ToolDefinition Tool { get; } = new(new("test-source", "write"), "write", "Test write",
        JsonSerializer.SerializeToElement(new { type = "object" }));

    public AgentRuntimeRegistration Register(IRunStateStore store) => new("test", "Test Agent", DefinitionId,
        new AgentRunner(DefinitionId, this, this, this, new(), policyEvaluator: new InMemoryPolicyEvaluator(
            new Dictionary<ToolId, PolicyDecision> { [Tool.Id] = new(Approval ? PolicyOutcome.RequireApproval : PolicyOutcome.Allow, "policy-v1", "Confirm frozen action.") }),
            runStore: store), this);

    public async Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken ct)
    {
        ModelEntered.TrySetResult();
        if (BlockModel) await ModelRelease.Task.WaitAsync(ct);
        if (FailModel) throw new InvalidOperationException("SECRET remote credential and internal error");
        return request.Messages.Count == 1
            ? new(null, [new("call-1", Tool.ModelName, JsonSerializer.SerializeToElement(new { value = "frozen" }))], ModelFinishReason.ToolCalls)
            : new(request.Messages[^1].ToolResult!.Disposition == ToolResultDisposition.RejectedByUser ? "Rejected." : "Finished.", [], ModelFinishReason.Completed);
    }
    public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ToolDefinition>>([Tool]);
    public async Task<ToolResult> ExecuteAsync(ToolDefinition tool, ToolCall call, CancellationToken ct)
    {
        Calls.Enqueue(call);
        ToolEntered.TrySetResult();
        if (BlockTool) await ToolRelease.Task.WaitAsync(ct);
        return new(call.CallId, true, JsonSerializer.SerializeToElement(new { status = "ok" }));
    }
    public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
}

internal sealed class StoreControl
{
    public bool HoldStart { get; init; }
    public bool FailStart { get; init; }
    public bool HoldClaim { get; init; }
    public bool FailClaim { get; init; }
    public Guid RunId;
    public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource StartRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ClaimEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ClaimRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class ControlledStore(IRunStateStore inner, StoreControl control) : IRunStateStore
{
    public async ValueTask CreateAsync(RunState state, IReadOnlyList<ExecutionEvent> events, CancellationToken ct)
    {
        control.RunId = state.RunId;
        control.StartEntered.TrySetResult();
        if (control.HoldStart) await control.StartRelease.Task.WaitAsync(ct);
        if (control.FailStart) throw new IOException("SECRET store creation failure");
        await inner.CreateAsync(state, events, ct);
    }
    public async ValueTask<bool> TryReplaceAsync(Guid id, long version, RunState state, IReadOnlyList<ExecutionEvent> events, CancellationToken ct)
    {
        if (events.Any(e => e.EventType == ExecutionEventType.ApprovalResolved))
        {
            control.ClaimEntered.TrySetResult();
            if (control.HoldClaim) await control.ClaimRelease.Task.WaitAsync(ct);
            if (control.FailClaim) throw new IOException("SECRET approval transaction failure");
        }
        return await inner.TryReplaceAsync(id, version, state, events, ct);
    }
    public ValueTask<RunState?> GetAsync(Guid id, CancellationToken ct) => inner.GetAsync(id, ct);
    public ValueTask AppendEventAsync(ExecutionEvent e, CancellationToken ct) => inner.AppendEventAsync(e, ct);
    public ValueTask<IReadOnlyList<ExecutionEvent>> ReadEventsAfterAsync(Guid id, long seq, int limit, CancellationToken ct) => inner.ReadEventsAfterAsync(id, seq, limit, ct);
}
