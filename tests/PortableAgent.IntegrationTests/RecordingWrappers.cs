using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;

namespace PortableAgent.IntegrationTests;

internal sealed class RecordingModel(IModelProvider inner) : IModelProvider
{
    public List<ModelRequest> Requests { get; } = [];
    public async Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return await inner.GenerateAsync(request, cancellationToken);
    }
}

internal sealed class RecordingToolProvider(IToolProvider inner) : IToolProvider
{
    public int Calls { get; private set; }
    public IReadOnlyList<ToolDefinition> Catalog { get; private set; } = [];
    public async Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken)
    {
        Calls++;
        Catalog = await inner.GetToolsAsync(cancellationToken);
        return Catalog;
    }
}

internal sealed class RecordingExecutor(IToolExecutor inner) : IToolExecutor
{
    public List<(ToolDefinition Definition, ToolCall Call, ToolResult Result)> Executions { get; } = [];
    public int Calls { get; private set; }
    public async Task<ToolResult> ExecuteAsync(
        ToolDefinition registeredTool, ToolCall call, CancellationToken cancellationToken)
    {
        Calls++;
        var result = await inner.ExecuteAsync(registeredTool, call, cancellationToken);
        Executions.Add((registeredTool, call, result));
        return result;
    }
}
