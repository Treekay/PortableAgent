using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace PortableAgent.Sample.McpServer;

[McpServerToolType]
public sealed class ServerStatusTool
{
    [McpServerTool(Name = "get_server_status", ReadOnly = true, UseStructuredContent = true)]
    [Description("Get the deterministic status of the sample service.")]
    public static ServerStatus GetServerStatus() => new("sample", "ok");
}

public sealed record ServerStatus(
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("status")] string Status);
