using PortableAgent.Core.Tools;

namespace PortableAgent.Core.Models;

public enum ModelFinishReason { Completed, ToolCalls }

public sealed record ModelRequest(
    IReadOnlyList<AgentMessage> Messages, IReadOnlyList<ToolDefinition> Tools);

public sealed record ModelReply(
    string? Text, IReadOnlyList<ToolCall> ToolCalls, ModelFinishReason FinishReason);

public interface IModelProvider
{
    Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken cancellationToken);
}
