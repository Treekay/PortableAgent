namespace PortableAgent.Core.Execution.Events;

public sealed class NullExecutionEventSink : IExecutionEventSink
{
    public static NullExecutionEventSink Instance { get; } = new();
    private NullExecutionEventSink() { }

    public ValueTask PublishAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
