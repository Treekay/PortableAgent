using PortableAgent.Core.Execution;

namespace PortableAgent.Infrastructure.Execution;

public sealed class InMemoryRunStateStore : IRunStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RunState> _runs = [];

    public ValueTask CreateAsync(RunState snapshot, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Version != 0) throw new ArgumentException("Initial version must be zero.");
            _runs.Add(snapshot.RunId, snapshot.Snapshot());
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
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (replacement.RunId != runId || replacement.Version != expectedVersion + 1)
                throw new ArgumentException("Invalid replacement identity or version.");
            if (!_runs.TryGetValue(runId, out var state) || state.Version != expectedVersion)
                return ValueTask.FromResult(false);
            _runs[runId] = replacement.Snapshot();
            return ValueTask.FromResult(true);
        }
    }
}
