using System.Collections.Concurrent;

namespace PortableAgent.Api.Hosting;

public enum RunOperationKind { Start, Approval }

/// <summary>Transient acknowledgments of committed Store operations, never an execution-state source.</summary>
public sealed class RunOperationSignals
{
    private readonly ConcurrentDictionary<Guid, Signal> _pending = new();

    public sealed class Signal(RunOperationKind kind)
    {
        internal RunOperationKind Kind { get; } = kind;
        internal TaskCompletionSource Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Committed => Source.Task;
    }

    public Signal Arm(Guid runId, RunOperationKind kind)
    {
        var signal = new Signal(kind);
        if (!_pending.TryAdd(runId, signal)) throw new InvalidOperationException("Run operation is already armed.");
        return signal;
    }

    public void Committed(Guid runId, RunOperationKind kind)
    {
        if (_pending.TryGetValue(runId, out var signal) && signal.Kind == kind)
            signal.Source.TrySetResult();
    }

    public void Remove(Guid runId, Signal signal) =>
        ((ICollection<KeyValuePair<Guid, Signal>>)_pending).Remove(new(runId, signal));
}
