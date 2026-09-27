using PortableAgent.Core.Tools;

namespace PortableAgent.Core.Policies;

public enum PolicyOutcome { Allow, Deny, RequireApproval }
public sealed record PolicyDecision(PolicyOutcome Outcome, string? PolicyId = null, string? Reason = null);
public sealed record PolicyEvaluationContext(Guid RunId, ToolDefinition Tool, ToolCall Call);
