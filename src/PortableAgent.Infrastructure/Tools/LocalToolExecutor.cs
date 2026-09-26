using System.Text.Json;
using PortableAgent.Core.Tools;

namespace PortableAgent.Infrastructure.Tools;

public sealed class LocalToolExecutor : IToolExecutor
{
    public Task<ToolResult> ExecuteAsync(
        ToolDefinition registeredTool, ToolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (registeredTool.Id != LocalToolProvider.CalculatorId)
            return Task.FromResult(new ToolResult(call.CallId, false, Error: "Unregistered local tool."));

        var arguments = call.Arguments;
        if (arguments.ValueKind != JsonValueKind.Object
            || arguments.EnumerateObject().Count() != 2
            || !arguments.TryGetProperty("a", out var a) || a.ValueKind != JsonValueKind.Number
            || !arguments.TryGetProperty("b", out var b) || b.ValueKind != JsonValueKind.Number
            || !a.TryGetDecimal(out var left) || !b.TryGetDecimal(out var right))
            return Task.FromResult(new ToolResult(call.CallId, false,
                Error: "Expected exactly two decimal-compatible numbers: a and b."));

        try
        {
            // SerializeToElement creates independently owned JSON storage.
            var output = JsonSerializer.SerializeToElement(new { result = left + right });
            return Task.FromResult(new ToolResult(call.CallId, true, output));
        }
        catch (OverflowException)
        {
            return Task.FromResult(new ToolResult(call.CallId, false, Error: "Sum exceeds the decimal range."));
        }
    }
}
