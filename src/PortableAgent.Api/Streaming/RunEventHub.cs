using PortableAgent.Core.Execution.Events;

namespace PortableAgent.Api.Streaming;

/// <summary>Ephemeral broadcast wake-ups only. SQLite owns the event history.</summary>
public sealed class RunEventHub(int capacity = 256) : IExecutionEventSink, IDisposable
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HashSet<RunEventSubscription>> _subscribers = [];
    private bool _stopped;

    public int SubscriberCount { get { lock (_gate) return _subscribers.Values.Sum(s => s.Count); } }

    public RunEventSubscription Subscribe(Guid runId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            var subscription = new RunEventSubscription(this, runId, _capacity);
            if (!_subscribers.TryGetValue(runId, out var subscribers)) _subscribers.Add(runId, subscribers = []);
            subscribers.Add(subscription);
            return subscription;
        }
    }

    public ValueTask PublishAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        RunEventSubscription[] subscribers;
        lock (_gate)
            subscribers = _subscribers.TryGetValue(executionEvent.RunId, out var group) ? group.ToArray() : [];
        foreach (var subscriber in subscribers) subscriber.Notify(executionEvent.Sequence);
        return ValueTask.CompletedTask;
    }

    internal void Remove(RunEventSubscription subscription)
    {
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(subscription.RunId, out var group)) return;
            group.Remove(subscription);
            if (group.Count == 0) _subscribers.Remove(subscription.RunId);
        }
    }

    public void Stop()
    {
        RunEventSubscription[] subscribers;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            subscribers = _subscribers.Values.SelectMany(s => s).ToArray();
            _subscribers.Clear();
        }
        foreach (var subscriber in subscribers) subscriber.Terminate();
    }

    public void Dispose() => Stop();
}
