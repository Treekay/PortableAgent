using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace PortableAgent.Sample.PetBoardingMcpServer;

/// <summary>Static business contract; the official SDK handles the MCP protocol.</summary>
public sealed class PetBoardingTools(PetBoardingState state)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ListToolsResult List() => new()
    {
        Tools =
        [
            new() { Name = "get_care_records", Description = "Read a pet's care records.", InputSchema = Schema(false) },
            new() { Name = "create_staff_task", Description = "Create a staff task for a known pet.", InputSchema = Schema(true) }
        ]
    };

    private static JsonElement Schema(bool write)
    {
        using var schema = JsonDocument.Parse(write
        ? """{"type":"object","properties":{"petName":{"type":"string"},"task":{"type":"string"}},"required":["petName","task"],"additionalProperties":false}"""
        : """{"type":"object","properties":{"petName":{"type":"string"}},"required":["petName"],"additionalProperties":false}""");
        return schema.RootElement.Clone();
    }

    public CallToolResult Call(CallToolRequestParams request)
    {
        try
        {
            if (request.Name is not ("get_care_records" or "create_staff_task"))
                throw new ArgumentException("Unknown Pet Boarding tool.");
            var write = request.Name == "create_staff_task";
            string[] fields = write ? ["petName", "task"] : ["petName"];
            var args = request.Arguments;
            if (args is null || args.Count != fields.Length || fields.Any(field =>
                !args.TryGetValue(field, out var value) || value.ValueKind != JsonValueKind.String))
                throw new ArgumentException("Expected exactly the declared string arguments.");
            object result = write
                ? state.CreateStaffTask(args["petName"].GetString()!, args["task"].GetString()!)
                : state.GetCareRecords(args["petName"].GetString()!);
            var output = JsonSerializer.SerializeToElement(result, Json);
            return new() { StructuredContent = output, Content = [new TextContentBlock { Text = output.GetRawText() }] };
        }
        catch (ArgumentException ex)
        {
            return new() { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
        }
    }
}
