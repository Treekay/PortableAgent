using System.Text.Json;

namespace PortableAgent.Core.Tools;

public sealed record ToolId(string SourceId, string Name);

public sealed record ToolDefinition(
    ToolId Id, string ModelName, string Description, JsonElement InputSchema);

public sealed record ToolCall(string CallId, string ToolName, JsonElement Arguments);

public sealed record ToolResult(
    string CallId, bool IsSuccess, JsonElement? Output = null, string? Error = null);

public interface IToolProvider
{
    Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken);
}

public interface IToolExecutor
{
    Task<ToolResult> ExecuteAsync(
        ToolDefinition registeredTool, ToolCall call, CancellationToken cancellationToken);
}
