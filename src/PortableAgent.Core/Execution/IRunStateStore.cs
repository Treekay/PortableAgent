namespace PortableAgent.Core.Execution;

public interface IRunStateStore
{
    ValueTask CreateAsync(RunState snapshot, CancellationToken cancellationToken);
    ValueTask<RunState?> GetAsync(Guid runId, CancellationToken cancellationToken);
    // Atomic compare-and-swap. replacement.Version must equal expectedVersion + 1.
    ValueTask<bool> TryReplaceAsync(Guid runId, long expectedVersion, RunState replacement,
        CancellationToken cancellationToken);
}
