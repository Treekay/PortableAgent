namespace PortableAgent.Core.Execution.Events;

public interface IExecutionEventSink
{
    // Called sequentially within one Run. A shared sink must handle concurrent Runs itself.
    // Implementations should return promptly. Delivery is best-effort, without retries.
    ValueTask PublishAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken);
}
