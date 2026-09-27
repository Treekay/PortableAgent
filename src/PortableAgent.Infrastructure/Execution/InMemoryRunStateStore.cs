using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;

namespace PortableAgent.Infrastructure.Execution;

public sealed class InMemoryRunStateStore : IRunStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RunState> _runs = [];
    private readonly List<ExecutionEvent> _events = [];

    public ValueTask CreateAsync(RunState snapshot, IReadOnlyList<ExecutionEvent> events, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Version != 0) throw new ArgumentException("Initial version must be zero.");
            ValidateEvents(snapshot.RunId, events);
            var ownedState = snapshot.Snapshot();
            var ownedEvents = events.Select(Clone).ToArray();
            _runs.Add(snapshot.RunId, ownedState);
            _events.AddRange(ownedEvents);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<RunState?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_runs.TryGetValue(runId, out var state) ? state.Snapshot() : null);
        }
    }

    public ValueTask<bool> TryReplaceAsync(Guid runId, long expectedVersion, RunState replacement,
        IReadOnlyList<ExecutionEvent> events, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (replacement.RunId != runId || replacement.Version != expectedVersion + 1)
                throw new ArgumentException("Invalid replacement identity or version.");
            if (!_runs.TryGetValue(runId, out var state) || state.Version != expectedVersion
                || !string.Equals(state.RuntimeDefinitionId, replacement.RuntimeDefinitionId, StringComparison.Ordinal)
                || (events.Any(e => e.EventType == ExecutionEventType.ApprovalResolved)
                    && state.Lifecycle != RunLifecycleState.AwaitingApproval))
                return ValueTask.FromResult(false);
            ValidateEvents(runId, events);
            var ownedState = replacement.Snapshot();
            var ownedEvents = events.Select(Clone).ToArray();
            _runs[runId] = ownedState;
            _events.AddRange(ownedEvents);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask AppendEventAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_runs.ContainsKey(executionEvent.RunId)) throw new InvalidOperationException("Run not found.");
            ValidateEvents(executionEvent.RunId, [executionEvent]);
            _events.Add(Clone(executionEvent));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<ExecutionEvent>> ReadEventsAfterAsync(Guid runId, long afterSequence,
        int limit, CancellationToken cancellationToken)
    {
        if (afterSequence < 0 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<ExecutionEvent>>(_events
                .Where(e => e.RunId == runId && e.Sequence > afterSequence).OrderBy(e => e.Sequence)
                .Take(limit).Select(Clone).ToArray());
        }
    }

    private void ValidateEvents(Guid runId, IReadOnlyList<ExecutionEvent> events)
    {
        var ids = _events.Select(e => e.EventId).ToHashSet();
        var sequences = _events.Where(e => e.RunId == runId).Select(e => e.Sequence).ToHashSet();
        foreach (var e in events)
            if (e.RunId != runId || e.EventId == Guid.Empty || e.Sequence <= 0
                || !ids.Add(e.EventId) || !sequences.Add(e.Sequence))
                throw new InvalidOperationException("Invalid or duplicate event identity/sequence.");
    }

    private static ExecutionEvent Clone(ExecutionEvent e) => e with { Payload = e.Payload.Clone() };
}
