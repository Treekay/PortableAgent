using System.Text.Json;
using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;
using PortableAgent.Infrastructure.Models;

namespace PortableAgent.Adapters.Mcp.Tests;

public sealed class ScriptedModelTests
{
    [Theory]
    [InlineData("call-id")]
    [InlineData("failed")]
    [InlineData("service")]
    [InlineData("status")]
    [InlineData("shape")]
    public async Task Script_does_not_confirm_incorrect_tool_results(string change)
    {
        var model = new ServerStatusScriptedModelProvider();
        var tools = new[] { new ToolDefinition(new("any-protocol", "get_server_status"), "get_server_status", "", JsonSerializer.SerializeToElement(new { type = "object" })) };
        var user = AgentMessage.User("Check the sample service status.");
        var proposal = await model.GenerateAsync(new([user], tools), default);
        var output = change == "shape" ? JsonSerializer.SerializeToElement("ok")
            : JsonSerializer.SerializeToElement(new { service = change == "service" ? "other" : "sample", status = change == "status" ? "down" : "ok" });
        var result = new ToolResult(change == "call-id" ? "wrong" : proposal.ToolCalls[0].CallId, change != "failed", output);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.GenerateAsync(new(
            [user, AgentMessage.AssistantToolRequest(null, proposal.ToolCalls), AgentMessage.FromToolResult(result)], tools), default));
    }
}
