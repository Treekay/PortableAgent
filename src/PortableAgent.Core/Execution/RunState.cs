using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;

namespace PortableAgent.Core.Execution;

public enum RunLifecycleState { Running, AwaitingApproval, Completed, Failed, Cancelled, LimitReached }

/// <summary>Owned execution data. Stores copy on both write and read; no live execution objects are retained.</summary>
public sealed class RunState
{
    public Guid RunId { get; init; }
    // Stable trusted composition identity, independent of process or user identity.
    public required string RuntimeDefinitionId { get; init; }
    public RunLifecycleState Lifecycle { get; set; } = RunLifecycleState.Running;
    public long Version { get; set; }
    public List<AgentMessage> Conversation { get; set; } = [];
    public List<ToolDefinition> ToolCatalog { get; set; } = [];
    public PendingApproval? PendingApproval { get; set; }
    public List<PendingApproval> ResolvedApprovals { get; set; } = [];
    public int ModelTurns { get; set; }
    public int ToolCalls { get; set; }
    public required RunLimits Limits { get; init; }
    public HashSet<string> UsedCallIds { get; set; } = new(StringComparer.Ordinal);
    public long LastSequence { get; set; }

    public RunState Snapshot() => new()
    {
        RunId = RunId, RuntimeDefinitionId = RuntimeDefinitionId, Lifecycle = Lifecycle, Version = Version,
        Conversation = Conversation.Select(message => message.Role switch
        {
            MessageRole.User => AgentMessage.User(message.Text!),
            MessageRole.Tool => AgentMessage.FromToolResult(message.ToolResult!),
            _ when message.ToolCalls.Count > 0 => AgentMessage.AssistantToolRequest(message.Text, message.ToolCalls),
            _ => AgentMessage.AssistantFinal(message.Text!)
        }).ToList(),
        ToolCatalog = ToolCatalog.Select(tool => tool with { InputSchema = tool.InputSchema.Clone() }).ToList(),
        PendingApproval = PendingApproval?.Snapshot(),
        ResolvedApprovals = ResolvedApprovals.Select(approval => approval.Snapshot()).ToList(),
        ModelTurns = ModelTurns, ToolCalls = ToolCalls, Limits = Limits,
        UsedCallIds = new(UsedCallIds, StringComparer.Ordinal), LastSequence = LastSequence
    };
}
