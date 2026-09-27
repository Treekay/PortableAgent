using System.Text.Json;
using ModelContextProtocol.Protocol;
using PortableAgent.Core.Tools;

namespace PortableAgent.Adapters.Mcp;

internal static class McpToolMapper
{
    public static ToolDefinition Definition(string sourceId, Tool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Name) || tool.InputSchema.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("MCP discovery returned an invalid tool name or input schema.");
        return new(new(sourceId, tool.Name), tool.Name, tool.Description ?? "", tool.InputSchema.Clone());
    }

    public static Dictionary<string, JsonElement> Arguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("MCP tool arguments must be a JSON object.");
        ValidateProperties(arguments);
        return arguments.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static void ValidateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate JSON argument property names are not supported.");
                ValidateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateProperties(item);
    }

    public static ToolResult Result(string callId, CallToolResult result)
    {
        // Structured content never licenses silently losing additional rich content.
        foreach (var block in result.Content)
            if (block is not TextContentBlock)
                throw new NotSupportedException($"Unsupported MCP content block: {block.Type}.");
        var texts = result.Content.Cast<TextContentBlock>().Select(b => b.Text).ToArray();
        var output = result.StructuredContent is { } structured
            ? structured.Clone()
            : JsonSerializer.SerializeToElement(new { content = texts.Select(t => new { type = "text", text = t }).ToArray() });
        var error = result.IsError == true
            ? (texts.Any(t => !string.IsNullOrWhiteSpace(t)) ? string.Join(Environment.NewLine, texts) : "MCP tool reported an execution error.")
            : null;
        return new(callId, result.IsError != true, output, error, ToolResultDisposition.Executed);
    }
}
