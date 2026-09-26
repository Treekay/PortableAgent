namespace PortableAgent.Core.Execution;

public sealed record AgentRunRequest(string UserMessage);
public sealed record RunLimits(int MaxModelTurns = 4, int MaxToolCalls = 4);
public enum RunStatus { Completed, Failed, Cancelled, LimitReached }
public sealed record AgentRunResult(RunStatus Status, string? FinalText = null, string? Error = null);
