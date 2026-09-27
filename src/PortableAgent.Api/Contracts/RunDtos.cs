using System.Text.Json;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Models;

namespace PortableAgent.Api.Contracts;

public sealed record PublicFailure(string Code, string Message);
public sealed record ToolIdentityDto(string SourceId, string Name);
public sealed record PendingApprovalDto(Guid ApprovalId, string ToolName, ToolIdentityDto ToolId,
    JsonElement Arguments, string? PolicyReason, DateTimeOffset CreatedAt);
public sealed record RunDto(Guid RunId, string? AgentId, string Status, int ModelTurns, int ToolCalls,
    long SnapshotSequence, bool IsActive, string? FinalText, PublicFailure? Failure, PendingApprovalDto? PendingApproval)
{
    public static RunDto FromState(RunState state, string? agentId, bool active)
    {
        var approval = state.Lifecycle == RunLifecycleState.AwaitingApproval ? state.PendingApproval : null;
        var final = state.Lifecycle == RunLifecycleState.Completed
            ? state.Conversation.LastOrDefault() : null;
        return new(state.RunId, agentId, state.Lifecycle.ToString(), state.ModelTurns, state.ToolCalls,
            state.LastSequence, active,
            final is { Role: MessageRole.Assistant, ToolCalls.Count: 0 } ? final.Text : null,
            state.Lifecycle == RunLifecycleState.Failed ? new("run_failed", "Run execution did not complete.") : null,
            approval is null ? null : new(approval.ApprovalId, approval.ToolCall.ToolName,
                new(approval.ToolDefinition.Id.SourceId, approval.ToolDefinition.Id.Name),
                approval.ToolCall.Arguments.Clone(), approval.PolicyReason, approval.CreatedAt));
    }
}
