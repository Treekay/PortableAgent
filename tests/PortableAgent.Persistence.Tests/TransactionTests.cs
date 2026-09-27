using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Core.Policies;
using PortableAgent.Persistence.Sqlite;
using PortableAgent.Persistence.Sqlite.Serialization;

namespace PortableAgent.Persistence.Tests;

public sealed class TransactionTests
{
    private static ExecutionEvent Event(Guid runId, long sequence, ExecutionEventType type) =>
        new(Guid.NewGuid(), runId, sequence, DateTimeOffset.UtcNow, type, JsonSerializer.SerializeToElement(new { }));

    [Fact]
    public async Task Failed_initial_event_rolls_back_run_creation()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.FailEventAsync(ExecutionEventType.RunStarted);
        var observed = new List<ExecutionEvent>();
        var composition = new Composition(db.Store(), sink: new EventSink(e => { observed.Add(e); return ValueTask.CompletedTask; }));
        var result = await composition.Runner.RunAsync(new("Cancel my booking."));
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Null(await db.Store().GetAsync(result.RunId, default));
        Assert.Empty(await db.Store().ReadEventsAfterAsync(result.RunId, 0, 100, default));
        Assert.Empty(composition.Executor.Calls);
        Assert.Empty(composition.Model.Requests);
        Assert.Empty(observed);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("state")]
    public async Task Pause_transaction_failure_commits_neither_checkpoint_nor_approval_event(string failure)
    {
        await using var db = await TestDatabase.CreateAsync();
        if (failure == "event") await db.FailEventAsync(ExecutionEventType.ApprovalRequired);
        else await db.SqlAsync("""
            CREATE TRIGGER fail_pause BEFORE UPDATE ON Runs WHEN NEW.Lifecycle = 'AwaitingApproval'
            BEGIN SELECT RAISE(ABORT, 'injected state failure'); END;
            """);
        var composition = new Composition(db.Store());
        var result = await composition.Runner.RunAsync(new("Cancel my booking."));
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Null(result.PendingApproval);
        Assert.Empty(composition.Executor.Calls);
        var saved = (await db.Store().GetAsync(result.RunId, default))!;
        Assert.Equal(RunLifecycleState.Failed, saved.Lifecycle);
        Assert.Null(saved.PendingApproval);
        Assert.Equal(1, saved.Version); // only terminal transition committed
        var events = await db.Store().ReadEventsAfterAsync(result.RunId, 0, 100, default);
        Assert.DoesNotContain(events, e => e.EventType == ExecutionEventType.ApprovalRequired);
        Assert.Equal(ExecutionEventType.RunFailed, events[^1].EventType);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("state")]
    public async Task Failed_approval_claim_rolls_back_state_and_both_events(string failure)
    {
        await using var db = await TestDatabase.CreateAsync();
        var paused = await new Composition(db.Store()).PauseAsync();
        var before = (await db.Store().GetAsync(paused.RunId, default))!;
        if (failure == "event") await db.FailEventAsync(ExecutionEventType.RunResumed);
        else await db.SqlAsync("""
            CREATE TRIGGER fail_claim BEFORE UPDATE ON Runs WHEN NEW.Lifecycle = 'Running'
            BEGIN SELECT RAISE(ABORT, 'injected claim failure'); END;
            """);
        var fresh = new Composition(db.Store());
        await Assert.ThrowsAnyAsync<Exception>(() => fresh.Runner.SubmitApprovalAsync(Composition.Command(paused)));
        Assert.Equal(RunStateSerializer.Serialize(before), RunStateSerializer.Serialize((await db.Store().GetAsync(paused.RunId, default))!));
        Assert.Empty(await db.Store().ReadEventsAfterAsync(paused.RunId, before.LastSequence, 100, default));
        Assert.Empty(fresh.Executor.Calls);
        Assert.Empty(fresh.Model.Requests);
    }

    [Theory]
    [InlineData(ExecutionEventType.ToolExecutionStarted, 0)]
    [InlineData(ExecutionEventType.ToolExecutionCompleted, 1)]
    public async Task Event_failure_stops_execution_without_retry_and_preserves_side_effect_uncertainty(
        ExecutionEventType failingEvent, int calls)
    {
        await using var db = await TestDatabase.CreateAsync();
        var paused = await new Composition(db.Store()).PauseAsync();
        await db.FailEventAsync(failingEvent);
        var fresh = new Composition(db.Store());
        var result = (await fresh.Runner.SubmitApprovalAsync(Composition.Command(paused))).RunResult!;
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(calls, fresh.Executor.Calls.Count);
        Assert.Empty(fresh.Model.Requests);
        var events = await db.Store().ReadEventsAfterAsync(paused.RunId, 0, 100, default);
        Assert.DoesNotContain(events, e => e.EventType == failingEvent);
        Assert.Equal(ExecutionEventType.RunFailed, events[^1].EventType);
        if (calls == 1) Assert.Contains("side effect may already have happened", result.Error!);
        Assert.Equal(ApprovalSubmissionStatus.Conflict,
            (await new Composition(db.Store()).Runner.SubmitApprovalAsync(Composition.Command(paused))).Status);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("state")]
    public async Task Terminal_transaction_failure_cannot_claim_durable_completion(string failure)
    {
        await using var db = await TestDatabase.CreateAsync();
        if (failure == "event") await db.FailEventAsync(ExecutionEventType.RunCompleted);
        else await db.SqlAsync("""
            CREATE TRIGGER fail_terminal BEFORE UPDATE ON Runs WHEN NEW.Lifecycle = 'Completed'
            BEGIN SELECT RAISE(ABORT, 'injected final state failure'); END;
            """);
        var observed = new List<ExecutionEvent>();
        var composition = new Composition(db.Store(), policy: new(PolicyOutcome.Allow),
            sink: new EventSink(e => { observed.Add(e); return ValueTask.CompletedTask; }));
        var result = await composition.Runner.RunAsync(new("Cancel my booking."));
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Single(composition.Executor.Calls);
        var saved = (await db.Store().GetAsync(result.RunId, default))!;
        Assert.Equal(RunLifecycleState.Running, saved.Lifecycle);
        Assert.Equal(0, saved.Version);
        var events = await db.Store().ReadEventsAfterAsync(result.RunId, 0, 100, default);
        Assert.DoesNotContain(events, e => e.EventType == ExecutionEventType.RunCompleted);
        Assert.Equal(events.Select(e => e.EventId), observed.Select(e => e.EventId));
    }

    [Theory]
    [InlineData("event-id")]
    [InlineData("sequence")]
    public async Task Database_enforces_unique_event_identity_and_run_sequence(string duplicate)
    {
        await using var db = await TestDatabase.CreateAsync();
        var paused = await new Composition(db.Store()).PauseAsync();
        var events = await db.Store().ReadEventsAfterAsync(paused.RunId, 0, 100, default);
        var existing = events[0];
        var collision = duplicate == "event-id" ? existing with { Sequence = 100 }
            : existing with { EventId = Guid.NewGuid() };
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Store().AppendEventAsync(collision, default).AsTask());
        Assert.Equal(events.Count, (await db.Store().ReadEventsAfterAsync(paused.RunId, 0, 100, default)).Count);
    }

    [Fact]
    public async Task Running_after_claim_is_not_recoverable_or_reapproved()
    {
        await using var db = await TestDatabase.CreateAsync();
        var paused = await new Composition(db.Store()).PauseAsync();
        var state = (await db.Store().GetAsync(paused.RunId, default))!;
        var version = state.Version++;
        state.ResolvedApprovals.Add(state.PendingApproval! with { Status = ApprovalStatus.Approved });
        state.PendingApproval = null;
        state.Lifecycle = RunLifecycleState.Running;
        var resolved = Event(state.RunId, ++state.LastSequence, ExecutionEventType.ApprovalResolved);
        var resumed = Event(state.RunId, ++state.LastSequence, ExecutionEventType.RunResumed);
        Assert.True(await db.Store().TryReplaceAsync(state.RunId, version, state, [resolved, resumed], default));
        // Simulate process disappearing after the durable claim, before an executor is invoked.
        var fresh = new Composition(db.Store());
        Assert.Equal(ApprovalSubmissionStatus.Conflict,
            (await fresh.Runner.SubmitApprovalAsync(Composition.Command(paused))).Status);
        Assert.Empty(fresh.Executor.Calls);
        Assert.Empty(fresh.Model.Requests);
        Assert.Equal(RunStateSerializer.Serialize(state), RunStateSerializer.Serialize((await db.Store().GetAsync(state.RunId, default))!));
    }

    [Fact]
    public async Task Store_recreation_reads_owned_snapshots_and_orders_history_by_sequence_not_timestamp()
    {
        await using var db = await TestDatabase.CreateAsync();
        var state = new RunState { RunId = Guid.NewGuid(), RuntimeDefinitionId = "test", Limits = new(), LastSequence = 3 };
        var one = Event(state.RunId, 1, ExecutionEventType.RunStarted);
        var two = Event(state.RunId, 2, ExecutionEventType.ToolDiscoveryStarted) with { OccurredAt = one.OccurredAt.AddDays(-2) };
        var three = Event(state.RunId, 3, ExecutionEventType.ToolDiscoveryCompleted) with { OccurredAt = one.OccurredAt.AddDays(-1) };
        await db.Store().CreateAsync(state, [three, one, two], default);
        var copy = (await db.Store().GetAsync(state.RunId, default))!;
        copy.UsedCallIds.Add("mutation");
        Assert.Empty((await db.Store().GetAsync(state.RunId, default))!.UsedCallIds);
        Assert.Equal(two.EventId, Assert.Single(await db.Store().ReadEventsAfterAsync(state.RunId, 1, 1, default)).EventId);
        Assert.Empty(await db.Store().ReadEventsAfterAsync(state.RunId, 3, 100, default));
        Assert.Equal(new long[] { 1, 2, 3 }, (await db.Store().ReadEventsAfterAsync(state.RunId, 0, 100, default)).Select(e => e.Sequence));
    }

    [Fact]
    public async Task Corrupted_relational_envelope_is_rejected()
    {
        await using var db = await TestDatabase.CreateAsync();
        var paused = await new Composition(db.Store()).PauseAsync();
        await db.SqlAsync("UPDATE Runs SET Version = Version + 1;");
        await Assert.ThrowsAsync<InvalidDataException>(() => db.Store().GetAsync(paused.RunId, default).AsTask());
    }

    [Fact]
    public async Task Initialization_uses_migration_history_and_is_repeatable_without_resetting_data()
    {
        await using var db = await TestDatabase.CreateAsync();
        var paused = await new Composition(db.Store()).PauseAsync();
        await SqliteRunStateStore.InitializeAsync(db.Path);
        Assert.Equal(paused.PendingApproval!.ApprovalId, (await db.Store().GetAsync(paused.RunId, default))!.PendingApproval!.ApprovalId);
        await db.SqlAsync("SELECT MigrationId FROM __EFMigrationsHistory;");
    }
}
