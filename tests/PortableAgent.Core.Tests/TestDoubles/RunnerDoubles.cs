using System.Text.Json;
using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;

namespace PortableAgent.Core.Tests.TestDoubles;

internal sealed class RecordingModel(Func<ModelRequest, ModelReply> script) : IModelProvider
{
    public List<ModelRequest> Requests { get; } = [];
    public Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(script(request));
    }
}

internal sealed class TestToolProvider : IToolProvider
{
    public static ToolDefinition Add { get; } = new(
        new ToolId("trusted-local", "calculator.add"), "calculator_add", "Adds two numbers.",
        JsonSerializer.SerializeToElement(new { type = "object" }));
    public IReadOnlyList<ToolDefinition> Definitions { get; init; } = [Add];
    public int Calls { get; private set; }
    public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(Definitions);
    }
}

internal sealed class RecordingExecutor : IToolExecutor
{
    public List<(ToolDefinition Definition, ToolCall Call)> Calls { get; } = [];
    public Func<ToolCall, ToolResult> Execute { get; init; } = call =>
        new(call.CallId, true, JsonSerializer.SerializeToElement(new { result = 5 }));
    public Task<ToolResult> ExecuteAsync(
        ToolDefinition registeredTool, ToolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add((registeredTool, call));
        return Task.FromResult(Execute(call));
    }
}
