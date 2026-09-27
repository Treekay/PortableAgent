using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Persistence.Sqlite.Entities;
using PortableAgent.Persistence.Sqlite.Serialization;

namespace PortableAgent.Persistence.Sqlite;

/// <summary>Short-lived contexts/transactions only. No model, tool or observer is called inside a transaction.</summary>
public sealed class SqliteRunStateStore(string databasePath) : IRunStateStore
{
    private readonly string _path = Path.GetFullPath(databasePath);

    public static async Task InitializeAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        await using var db = PortableAgentDbContextFactory.Open(databasePath, create: true);
        await db.Database.MigrateAsync(cancellationToken);
    }

    public async ValueTask CreateAsync(RunState snapshot, IReadOnlyList<ExecutionEvent> events, CancellationToken cancellationToken)
    {
        if (snapshot.Version != 0) throw new ArgumentException("Initial version must be zero.");
        ValidateBatch(snapshot, events);
        var json = RunStateSerializer.Serialize(snapshot);
        var rows = events.Select(ToEntity).ToArray();
        var run = new RunStateEntity { RunId = snapshot.RunId, RuntimeDefinitionId = snapshot.RuntimeDefinitionId,
            Lifecycle = snapshot.Lifecycle.ToString(), Version = snapshot.Version, UpdatedAt = DateTimeOffset.UtcNow, StateJson = json };
        await using var db = PortableAgentDbContextFactory.Open(_path);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Runs.Add(run);
        db.ExecutionEvents.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    public async ValueTask<RunState?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var db = PortableAgentDbContextFactory.Open(_path);
        var row = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.RunId == runId, cancellationToken);
        if (row is null) return null;
        var state = RunStateSerializer.Deserialize(row.StateJson);
        if (state.RunId != row.RunId || state.Version != row.Version || state.Lifecycle.ToString() != row.Lifecycle
            || !string.Equals(state.RuntimeDefinitionId, row.RuntimeDefinitionId, StringComparison.Ordinal))
            throw new InvalidDataException("Run envelope does not match its snapshot.");
        return state;
    }

    public async ValueTask<bool> TryReplaceAsync(Guid runId, long expectedVersion, RunState replacement,
        IReadOnlyList<ExecutionEvent> events, CancellationToken cancellationToken)
    {
        if (replacement.RunId != runId || replacement.Version != expectedVersion + 1)
            throw new ArgumentException("Invalid replacement identity or version.");
        ValidateBatch(replacement, events);
        var json = RunStateSerializer.Serialize(replacement);
        var rows = events.Select(ToEntity).ToArray();
        var approvalClaim = events.Any(e => e.EventType == ExecutionEventType.ApprovalResolved);
        var nextVersion = replacement.Version;
        var lifecycle = replacement.Lifecycle.ToString();
        var now = DateTimeOffset.UtcNow;
        await using var db = PortableAgentDbContextFactory.Open(_path);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var query = db.Runs.Where(r => r.RunId == runId && r.Version == expectedVersion
            && r.RuntimeDefinitionId == replacement.RuntimeDefinitionId);
        if (approvalClaim)
            query = query.Where(r => r.Lifecycle == "AwaitingApproval");
        var affected = await query.ExecuteUpdateAsync(setters => setters
            .SetProperty(r => r.Version, nextVersion).SetProperty(r => r.Lifecycle, lifecycle)
            .SetProperty(r => r.StateJson, json).SetProperty(r => r.UpdatedAt, now), cancellationToken);
        if (affected != 1) return false;
        db.ExecutionEvents.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    public async ValueTask AppendEventAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        await using var db = PortableAgentDbContextFactory.Open(_path);
        db.ExecutionEvents.Add(ToEntity(executionEvent));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<ExecutionEvent>> ReadEventsAfterAsync(Guid runId, long afterSequence,
        int limit, CancellationToken cancellationToken)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "Use 1 through 1000.");
        await using var db = PortableAgentDbContextFactory.Open(_path);
        var rows = await db.ExecutionEvents.AsNoTracking().Where(e => e.RunId == runId && e.Sequence > afterSequence)
            .OrderBy(e => e.Sequence).Take(limit).ToArrayAsync(cancellationToken);
        return rows.Select(e =>
        {
            using var payload = JsonDocument.Parse(e.PayloadJson);
            return new ExecutionEvent(e.EventId, e.RunId, e.Sequence, e.OccurredAt,
                RunStateSerializer.ParseEnum<ExecutionEventType>(e.EventType), payload.RootElement.Clone());
        }).ToArray();
    }

    private static void ValidateBatch(RunState state, IReadOnlyList<ExecutionEvent> events)
    {
        if (state.RunId == Guid.Empty || string.IsNullOrWhiteSpace(state.RuntimeDefinitionId) || state.Version < 0)
            throw new ArgumentException("Invalid Run identity/version.");
        if (events.Any(e => e.RunId != state.RunId || e.Sequence > state.LastSequence))
            throw new ArgumentException("Events must belong to the snapshot and its allocated sequence.");
    }

    private static ExecutionEventEntity ToEntity(ExecutionEvent e)
    {
        if (e.EventId == Guid.Empty || e.RunId == Guid.Empty || e.Sequence <= 0 || !Enum.IsDefined(e.EventType))
            throw new ArgumentException("Invalid event identity, sequence or type.");
        return new() { EventId = e.EventId, RunId = e.RunId, Sequence = e.Sequence, OccurredAt = e.OccurredAt,
            EventType = e.EventType.ToString(), PayloadJson = e.Payload.GetRawText() };
    }
}
