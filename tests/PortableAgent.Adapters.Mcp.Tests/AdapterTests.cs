using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using PortableAgent.Core.Tools;

namespace PortableAgent.Adapters.Mcp.Tests;

public sealed class AdapterTests
{
    private static McpToolAdapter Adapter(TestServerFixture server) => new(new("sample-mcp", server.Endpoint));
    private static ToolCall Call(ToolDefinition tool, string json = "{}") => new("local-call-123", tool.ModelName, JsonSerializer.Deserialize<JsonElement>(json));

    [Fact]
    public async Task Real_sample_discovery_and_call_map_owned_core_data()
    {
        await using var server = await TestServerFixture.StartAsync(sampleTools: true);
        ToolDefinition tool;
        ToolResult result;
        await using (var adapter = Adapter(server))
        {
            tool = Assert.Single(await adapter.GetToolsAsync(default));
            Assert.Equal(new("sample-mcp", "get_server_status"), tool.Id);
            Assert.Equal("get_server_status", tool.ModelName);
            Assert.Equal("Get the deterministic status of the sample service.", tool.Description);
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
            result = await adapter.ExecuteAsync(tool, Call(tool), default);
        }
        Assert.True(result.IsSuccess);
        Assert.Equal(ToolResultDisposition.Executed, result.Disposition);
        Assert.Equal("local-call-123", result.CallId);
        Assert.Equal("ok", result.Output!.Value.GetProperty("status").GetString());
        Assert.Equal("sample", result.Output.Value.GetProperty("service").GetString());
        Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind);
        Assert.Contains("tools/list", server.Methods);
        Assert.Contains("tools/call", server.Methods);
    }

    [Fact]
    public async Task Rediscovery_refreshes_catalog_without_reinitializing_client()
    {
        await using var server = await TestServerFixture.StartAsync();
        await using var adapter = Adapter(server);
        var first = Assert.Single(await adapter.GetToolsAsync(default));
        var setupCount = server.Methods.Count(m => m is not ("tools/list" or "tools/call"));
        server.Tools = [TestServerFixture.Definition("changed_status")];
        var second = Assert.Single(await adapter.GetToolsAsync(default));
        Assert.Equal("changed_status", second.ModelName);
        Assert.Equal(2, server.Discoveries);
        Assert.Equal(setupCount, server.Methods.Count(m => m is not ("tools/list" or "tools/call")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ExecuteAsync(first, Call(first), default));
        Assert.Empty(server.Calls);
        await adapter.ExecuteAsync(second, Call(second), default);
        Assert.Equal("changed_status", Assert.Single(server.Calls).Name);
    }

    [Fact]
    public async Task Failed_refresh_does_not_return_or_execute_stale_catalog()
    {
        await using var server = await TestServerFixture.StartAsync();
        await using var adapter = Adapter(server);
        var tool = Assert.Single(await adapter.GetToolsAsync(default));
        server.FailDiscovery = true;
        await Assert.ThrowsAnyAsync<Exception>(() => adapter.GetToolsAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ExecuteAsync(tool, Call(tool), default));
        Assert.Empty(server.Calls);
        Assert.Equal(2, server.Discoveries);
    }

    [Fact]
    public async Task Duplicate_discovered_names_fail_without_renaming_or_overwriting()
    {
        await using var server = await TestServerFixture.StartAsync();
        server.Tools = [TestServerFixture.Definition(), TestServerFixture.Definition()];
        await using var adapter = Adapter(server);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetToolsAsync(default));
        Assert.Contains("Duplicate", error.Message);
        Assert.Empty(server.Calls);
    }

    [Theory]
    [InlineData("{\"ok\":true}", JsonValueKind.Object)]
    [InlineData("[1,\"two\",null]", JsonValueKind.Array)]
    [InlineData("\"online\"", JsonValueKind.String)]
    [InlineData("123456789012345678901234567890.123456", JsonValueKind.Number)]
    [InlineData("true", JsonValueKind.True)]
    [InlineData("null", JsonValueKind.Null)]
    public async Task Structured_results_preserve_json_kind_and_value(string json, JsonValueKind kind)
    {
        await using var server = await TestServerFixture.StartAsync();
        server.Handler = (_, _) => ValueTask.FromResult(new CallToolResult
        {
            StructuredContent = JsonSerializer.Deserialize<JsonElement>(json),
            Content = [new TextContentBlock { Text = "compatibility text" }]
        });
        await using var adapter = Adapter(server);
        var tool = Assert.Single(await adapter.GetToolsAsync(default));
        var result = await adapter.ExecuteAsync(tool, Call(tool), default);
        Assert.Equal(kind, result.Output!.Value.ValueKind);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(json), result.Output.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Text_fallback_preserves_order_without_parsing_and_supports_empty(bool empty)
    {
        await using var server = await TestServerFixture.StartAsync();
        server.Handler = (_, _) => ValueTask.FromResult(new CallToolResult
        {
            Content = empty ? [] : [new TextContentBlock { Text = "{\"ok\":true}" }, new TextContentBlock { Text = "second" }]
        });
        await using var adapter = Adapter(server);
        var tool = Assert.Single(await adapter.GetToolsAsync(default));
        var result = await adapter.ExecuteAsync(tool, Call(tool), default);
        var content = result.Output!.Value.GetProperty("content");
        Assert.Equal(empty ? 0 : 2, content.GetArrayLength());
        if (!empty)
        {
            Assert.Equal("{\"ok\":true}", content[0].GetProperty("text").GetString());
            Assert.Equal("second", content[1].GetProperty("text").GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rich_content_is_rejected_even_alongside_structured_content(bool structured)
    {
        await using var server = await TestServerFixture.StartAsync();
        server.Handler = (_, _) => ValueTask.FromResult(new CallToolResult
        {
            StructuredContent = structured ? JsonSerializer.SerializeToElement(new { ok = true }) : null,
            Content = [new ImageContentBlock { Data = "AA=="u8.ToArray(), MimeType = "image/png" }]
        });
        await using var adapter = Adapter(server);
        var tool = Assert.Single(await adapter.GetToolsAsync(default));
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.ExecuteAsync(tool, Call(tool), default));
        Assert.Single(server.Calls);
    }

    [Fact]
    public async Task Nested_arguments_are_structured_and_call_id_stays_local()
    {
        await using var server = await TestServerFixture.StartAsync();
        server.Handler = (request, _) => ValueTask.FromResult(new CallToolResult { StructuredContent = JsonSerializer.SerializeToElement(request.Arguments) });
        await using var adapter = Adapter(server);
        var tool = Assert.Single(await adapter.GetToolsAsync(default));
        var call = Call(tool, """{"object":{"list":[1,"text",true,null,{"n":123456789012345678901234567890.123456}]}}""");
        var result = await adapter.ExecuteAsync(tool, call, default);
        Assert.True(JsonElement.DeepEquals(call.Arguments, result.Output!.Value));
        Assert.Equal(call.CallId, result.CallId);
        Assert.Single(Assert.Single(server.Calls).Arguments!);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("{\"x\":1,\"x\":2}")]
    [InlineData("{\"nested\":[{\"x\":1,\"x\":2}]}")]
    public async Task Invalid_arguments_are_rejected_before_remote_execution(string json)
    {
        await using var server = await TestServerFixture.StartAsync();
        await using var adapter = Adapter(server);
        var tool = Assert.Single(await adapter.GetToolsAsync(default));
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.ExecuteAsync(tool, Call(tool, json), default));
        Assert.Empty(server.Calls);
        Assert.DoesNotContain("tools/call", server.Methods);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("source-case")]
    [InlineData("identity")]
    [InlineData("model-name")]
    [InlineData("schema")]
    [InlineData("call-name")]
    public async Task Untrusted_or_inconsistent_routing_never_reaches_server(string change)
    {
        await using var server = await TestServerFixture.StartAsync();
        await using var adapter = Adapter(server);
        var original = Assert.Single(await adapter.GetToolsAsync(default));
        var tool = change switch
        {
            "source" => original with { Id = original.Id with { SourceId = "another-source" } },
            "source-case" => original with { Id = original.Id with { SourceId = "SAMPLE-MCP" } },
            "identity" => original with { Id = original.Id with { Name = "unknown" } },
            "model-name" => original with { ModelName = "renamed" },
            "schema" => original with { InputSchema = JsonSerializer.SerializeToElement(new { type = "string" }) },
            _ => original
        };
        var call = Call(tool) with { ToolName = change == "call-name" ? "forged" : tool.ModelName };
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ExecuteAsync(tool, call, default));
        Assert.Empty(server.Calls);
        Assert.DoesNotContain("tools/call", server.Methods);
    }

    [Fact]
    public async Task Mapping_tool_error_is_an_executed_failure()
    {
        await using var server = await TestServerFixture.StartAsync();
        server.Handler = (_, _) => ValueTask.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "Service unavailable." }] });
        await using var adapter = Adapter(server);
        var tool = Assert.Single(await adapter.GetToolsAsync(default));
        var result = await adapter.ExecuteAsync(tool, Call(tool), default);
        Assert.False(result.IsSuccess);
        Assert.Equal(ToolResultDisposition.Executed, result.Disposition);
        Assert.Equal("Service unavailable.", result.Error);
    }
}
