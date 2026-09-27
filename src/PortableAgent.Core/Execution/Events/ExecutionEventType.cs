namespace PortableAgent.Core.Execution.Events;

public enum ExecutionEventType
{
    RunStarted,
    ToolDiscoveryStarted,
    ToolDiscoveryCompleted,
    ModelTurnStarted,
    ModelTurnCompleted,
    ToolCallProposed,
    ToolExecutionStarted,
    ToolExecutionCompleted,
    RunCompleted,
    RunFailed,
    RunCancelled,
    RunLimitReached
}
