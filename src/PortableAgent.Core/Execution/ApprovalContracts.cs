using PortableAgent.Core.Tools;

namespace PortableAgent.Core.Execution;

public enum ApprovalStatus { Pending, Approved, Rejected }
public enum ApprovalChoice { Approve, Reject }
public sealed record PendingApproval(
    Guid ApprovalId, Guid RunId, ToolDefinition ToolDefinition, ToolCall ToolCall,
    string? PolicyId, string? PolicyReason, DateTimeOffset CreatedAt, ApprovalStatus Status)
{
    public PendingApproval Snapshot() => this with
    {
        ToolDefinition = ToolDefinition with { InputSchema = ToolDefinition.InputSchema.Clone() },
        ToolCall = ToolCall with { Arguments = ToolCall.Arguments.Clone() }
    };
}

public sealed record ApprovalCommand(Guid RunId, Guid ApprovalId, ApprovalChoice Decision);
public enum ApprovalSubmissionStatus { Accepted, NotFound, Conflict }
public sealed record ApprovalSubmissionResult(ApprovalSubmissionStatus Status, AgentRunResult? RunResult = null);
