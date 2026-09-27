namespace PortableAgent.Core.Execution.Events;

public enum ExecutionEventType
{
    RunStarted,
    ToolDiscoveryStarted,
    ToolDiscoveryCompleted,
    ModelTurnStarted,
    ModelTurnCompleted,
    ToolCallProposed,
    PolicyEvaluationStarted,
    PolicyEvaluationCompleted,
    ApprovalRequired,
    ApprovalResolved,
    RunResumed,
    ToolExecutionStarted,
    ToolExecutionCompleted,
    RunCompleted,
    RunFailed,
    RunCancelled,
    RunLimitReached
}
