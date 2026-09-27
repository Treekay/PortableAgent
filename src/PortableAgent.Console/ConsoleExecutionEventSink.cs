using PortableAgent.Core.Execution.Events;

namespace PortableAgent.Console;

/// <summary>A local rendering consumer; it knows the event contract, not Runner internals.</summary>
internal sealed class ConsoleExecutionEventSink : IExecutionEventSink
{
    public ValueTask PublishAsync(ExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = executionEvent.Payload;
        var text = executionEvent.EventType switch
        {
            ExecutionEventType.RunStarted => "Run started",
            ExecutionEventType.ToolDiscoveryStarted => "Discovering tools",
            ExecutionEventType.ToolDiscoveryCompleted => $"Discovered {payload.GetProperty("toolCount").GetInt32()} tools",
            ExecutionEventType.ModelTurnStarted => $"Model turn {payload.GetProperty("turn").GetInt32()} started",
            ExecutionEventType.ModelTurnCompleted => $"Model turn {payload.GetProperty("turn").GetInt32()} completed · {payload.GetProperty("finishReason").GetString()}",
            ExecutionEventType.ToolCallProposed => $"Tool proposed · {payload.GetProperty("toolName").GetString()}",
            ExecutionEventType.ToolExecutionStarted => $"Tool execution started · {payload.GetProperty("toolName").GetString()}",
            ExecutionEventType.ToolExecutionCompleted => $"Tool execution completed · {payload.GetProperty("toolName").GetString()} · success={payload.GetProperty("success").GetBoolean()}",
            ExecutionEventType.RunCompleted => $"Run completed · {payload.GetProperty("modelTurns").GetInt32()} model turns · {payload.GetProperty("toolCalls").GetInt32()} tool call(s)",
            ExecutionEventType.RunFailed => "Run failed",
            ExecutionEventType.RunCancelled => "Run cancelled",
            ExecutionEventType.RunLimitReached => "Run limit reached",
            _ => executionEvent.EventType.ToString()
        };
        System.Console.WriteLine($"[{executionEvent.Sequence}] {text}");
        return ValueTask.CompletedTask;
    }
}
