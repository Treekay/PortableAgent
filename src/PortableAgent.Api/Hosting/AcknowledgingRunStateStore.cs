using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;

namespace PortableAgent.Api.Hosting;

/// <summary>Signals only after the real Store commits. Does not write additional events or snapshots.</summary>
public sealed class AcknowledgingRunStateStore(IRunStateStore inner, RunOperationSignals signals) : IRunStateStore
{
    public async ValueTask CreateAsync(RunState state, IReadOnlyList<ExecutionEvent> events, CancellationToken ct)
    {
        await inner.CreateAsync(state, events, ct);
        if (events.Any(e => e.EventType == ExecutionEventType.RunStarted))
            signals.Committed(state.RunId, RunOperationKind.Start);
    }

    public async ValueTask<bool> TryReplaceAsync(Guid runId, long version, RunState replacement,
        IReadOnlyList<ExecutionEvent> events, CancellationToken ct)
    {
        var committed = await inner.TryReplaceAsync(runId, version, replacement, events, ct);
        if (committed && events.Any(e => e.EventType == ExecutionEventType.ApprovalResolved)
            && events.Any(e => e.EventType == ExecutionEventType.RunResumed))
            signals.Committed(runId, RunOperationKind.Approval);
        return committed;
    }

    public ValueTask<RunState?> GetAsync(Guid id, CancellationToken ct) => inner.GetAsync(id, ct);
    public ValueTask AppendEventAsync(ExecutionEvent e, CancellationToken ct) => inner.AppendEventAsync(e, ct);
    public ValueTask<IReadOnlyList<ExecutionEvent>> ReadEventsAfterAsync(Guid id, long sequence, int limit, CancellationToken ct) =>
        inner.ReadEventsAfterAsync(id, sequence, limit, ct);
}
