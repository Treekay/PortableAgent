using System.Threading.Channels;

namespace PortableAgent.Api.Streaming;

public sealed class RunEventSubscription : IAsyncDisposable
{
    private readonly RunEventHub _hub;
    private readonly Channel<long> _channel;
    private readonly CancellationTokenSource _termination = new();
    private readonly object _gate = new();
    private Task? _cancellation;
    private bool _ended;
    private int _disposed;

    internal RunEventSubscription(RunEventHub hub, Guid runId, int capacity)
    {
        _hub = hub;
        RunId = runId;
        Terminated = _termination.Token;
        _channel = Channel.CreateBounded<long>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public Guid RunId { get; }
    public ChannelReader<long> Notifications => _channel.Reader;
    public CancellationToken Terminated { get; }
    public bool Overflowed { get; private set; }

    internal void Notify(long sequence)
    {
        lock (_gate)
        {
            if (_ended) return;
            if (!_channel.Writer.TryWrite(sequence)) End(overflowed: true);
        }
    }

    internal void Terminate() { lock (_gate) End(overflowed: false); }

    private void End(bool overflowed)
    {
        if (_ended) return;
        _ended = true;
        Overflowed = overflowed;
        _hub.Remove(this);
        _channel.Writer.TryComplete();
        // Set cancellation now, but never run arbitrary HTTP cancellation callbacks on the Runner's path.
        _cancellation = _termination.CancelAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Terminate();
        try { await _cancellation!; }
        finally { _termination.Dispose(); }
    }
}
