using System.Text.Json;
using PortableAgent.Core.Tools;

namespace PortableAgent.Infrastructure.Tools;

public sealed class LocalToolProvider : IToolProvider
{
    internal static readonly ToolId CalculatorId = new("local", "calculator.add");

    public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": { "a": { "type": "number" }, "b": { "type": "number" } },
              "required": ["a", "b"],
              "additionalProperties": false
            }
            """);
        // Clone because the temporary document is disposed before the schema is consumed.
        IReadOnlyList<ToolDefinition> tools = [new(
            CalculatorId, "calculator_add", "Adds two numbers.", document.RootElement.Clone())];
        return Task.FromResult(tools);
    }
}
