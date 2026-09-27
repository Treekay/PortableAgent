using PortableAgent.Core.Execution.Events;

namespace PortableAgent.Core.Execution;

public interface IRunStateStore
{
    // State and supplied events commit atomically. Live delivery is a separate concern.
    ValueTask CreateAsync(RunState snapshot, IReadOnlyList<ExecutionEvent> events, CancellationToken cancellationToken);
    ValueTask<RunState?> GetAsync(Guid runId, CancellationToken cancellationToken);
    // Atomic compare-and-swap. replacement.Version must equal expectedVersion + 1.
    ValueTask<bool> TryReplaceAsync(Guid runId, long expectedVersion, RunState replacement, IReadOnlyList<ExecutionEvent> events,
        CancellationToken cancellationToken);
    ValueTask AppendEventAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ExecutionEvent>> ReadEventsAfterAsync(Guid runId, long afterSequence, int limit,
        CancellationToken cancellationToken);
}
