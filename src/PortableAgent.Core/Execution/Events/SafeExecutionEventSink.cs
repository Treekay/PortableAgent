namespace PortableAgent.Core.Execution.Events;

/// <summary>Per-Run observer isolation. Diagnostics never become the Agent execution outcome.</summary>
internal sealed class SafeExecutionEventSink(IExecutionEventSink inner, CancellationToken runCancellationToken)
    : IExecutionEventSink
{
    // Available for local debugging, deliberately not exposed through AgentRunResult.
    internal int FailureCount { get; private set; }

    public async ValueTask PublishAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        try
        {
            await inner.PublishAsync(executionEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (runCancellationToken.IsCancellationRequested)
        {
            // Expected Run cancellation; AgentRunner still determines its own terminal status.
        }
        catch (Exception)
        {
            // Includes sink-originated cancellation when the Run itself was not cancelled.
            // No retries, recursive failure events or changes to the execution result.
            FailureCount++;
        }
    }
}
