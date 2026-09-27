using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Core.Models;
using PortableAgent.Core.Policies;
using PortableAgent.Core.Tools;
using PortableAgent.Infrastructure.Models;
using PortableAgent.Infrastructure.Policies;
using PortableAgent.Infrastructure.Tools.FlightBooking;
using PortableAgent.Persistence.Sqlite;

namespace PortableAgent.Persistence.Tests;

internal sealed class TestDatabase : IAsyncDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"portable-agent-test-{Guid.NewGuid():N}.db");
    public SqliteRunStateStore Store() => new(Path);
    public static async Task<TestDatabase> CreateAsync()
    {
        var database = new TestDatabase();
        await SqliteRunStateStore.InitializeAsync(database.Path);
        return database;
    }
    public async Task SqlAsync(string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    public Task FailEventAsync(ExecutionEventType type) => SqlAsync($"""
        CREATE TRIGGER fail_event BEFORE INSERT ON ExecutionEvents
        WHEN NEW.EventType = '{type}' BEGIN SELECT RAISE(ABORT, 'injected event failure'); END;
        """);
    public ValueTask DisposeAsync()
    {
        // Only these uniquely owned files; pooling is disabled, all operation contexts are disposed.
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(Path + suffix);
        return ValueTask.CompletedTask;
    }
}

internal sealed class Composition
{
    public const string DefinitionId = "flight-demo-v1";
    public static readonly PolicyDecision Confirmation = new(PolicyOutcome.RequireApproval, "confirm-v1", "Confirm cancellation");
    public RecordingModel Model { get; } = new();
    public RecordingExecutor Executor { get; } = new();
    public AgentRunner Runner { get; }
    public Composition(IRunStateStore store, string definitionId = DefinitionId, IExecutionEventSink? sink = null,
        IToolProvider? tools = null, PolicyDecision? policy = null, RunLimits? limits = null)
    {
        Runner = new(definitionId, Model, tools ?? new FlightBookingToolProvider(), Executor, limits ?? new(), sink,
            new InMemoryPolicyEvaluator(new Dictionary<ToolId, PolicyDecision>
                { [new("flight-local", "booking.cancel")] = policy ?? Confirmation }), store);
    }
    public async Task<AgentRunResult> PauseAsync()
    {
        var result = await Runner.RunAsync(new("Cancel my booking."));
        Assert.Equal(RunStatus.AwaitingApproval, result.Status);
        Assert.Empty(Executor.Calls);
        return result;
    }
    public static ApprovalCommand Command(AgentRunResult paused, ApprovalChoice choice = ApprovalChoice.Approve) =>
        new(paused.RunId, paused.PendingApproval!.ApprovalId, choice);
}

internal sealed class RecordingModel : IModelProvider
{
    private readonly FlightBookingScriptedModelProvider _inner = new();
    public List<ModelRequest> Requests { get; } = [];
    public Func<ModelRequest, ModelReply>? Script { get; set; }
    public Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Script is null ? _inner.GenerateAsync(request, cancellationToken) : Task.FromResult(Script(request));
    }
}

internal sealed class RecordingExecutor : IToolExecutor
{
    private readonly FlightBookingToolExecutor _inner = new();
    public ConcurrentQueue<ToolCall> Calls { get; } = new();
    public Task<ToolResult> ExecuteAsync(ToolDefinition tool, ToolCall call, CancellationToken cancellationToken)
    {
        Calls.Enqueue(call);
        return _inner.ExecuteAsync(tool, call, cancellationToken);
    }
}

internal sealed class EventSink(Func<ExecutionEvent, ValueTask> observe) : IExecutionEventSink
{
    public ValueTask PublishAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken) => observe(executionEvent);
}

internal sealed class Catalog(IReadOnlyList<ToolDefinition> tools) : IToolProvider
{
    public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken) => Task.FromResult(tools);
}

// Test-only decorator ensures both independent SQLite stores return the same paused version before CAS.
internal sealed class ReadBarrierStore(IRunStateStore inner, Barrier barrier) : IRunStateStore
{
    public ValueTask CreateAsync(RunState s, IReadOnlyList<ExecutionEvent> e, CancellationToken ct) => inner.CreateAsync(s, e, ct);
    public async ValueTask<RunState?> GetAsync(Guid id, CancellationToken ct)
    {
        var state = await inner.GetAsync(id, ct);
        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10), ct)) throw new TimeoutException("Concurrent read barrier timed out.");
        return state;
    }
    public ValueTask<bool> TryReplaceAsync(Guid id, long version, RunState s, IReadOnlyList<ExecutionEvent> e, CancellationToken ct) =>
        inner.TryReplaceAsync(id, version, s, e, ct);
    public ValueTask AppendEventAsync(ExecutionEvent e, CancellationToken ct) => inner.AppendEventAsync(e, ct);
    public ValueTask<IReadOnlyList<ExecutionEvent>> ReadEventsAfterAsync(Guid id, long after, int limit, CancellationToken ct) =>
        inner.ReadEventsAfterAsync(id, after, limit, ct);
}
