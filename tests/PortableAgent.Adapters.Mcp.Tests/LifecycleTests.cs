using System.Text.Json;
using ModelContextProtocol.Protocol;
using PortableAgent.Core.Tools;

namespace PortableAgent.Adapters.Mcp.Tests;

public sealed class LifecycleTests
{
    [Theory]
    [InlineData("", "http://localhost/mcp")]
    [InlineData(" ", "http://localhost/mcp")]
    [InlineData("sample", "mcp")]
    [InlineData("sample", "file:///sample")]
    public void Invalid_trusted_configuration_is_rejected_without_network(string source, string uri)
    {
        Assert.ThrowsAny<ArgumentException>(() => new McpToolAdapter(new(source, new Uri(uri, UriKind.RelativeOrAbsolute))));
    }

    [Fact]
    public async Task Execution_requires_discovery_and_disposal_prevents_initialization()
    {
        await using var server = await TestServerFixture.StartAsync();
        var adapter = new McpToolAdapter(new("sample-mcp", server.Endpoint));
        var tool = new ToolDefinition(new("sample-mcp", "get_server_status"), "get_server_status", "", JsonSerializer.SerializeToElement(new { type = "object" }));
        var call = new ToolCall("one", tool.ModelName, JsonSerializer.SerializeToElement(new { }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ExecuteAsync(tool, call, default));
        await adapter.DisposeAsync();
        await adapter.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.GetToolsAsync(default));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.ExecuteAsync(tool, call, default));
        Assert.Empty(server.Methods);
    }

    [Fact]
    public async Task Gate_serializes_execution_discovery_and_disposal_and_waiting_is_cancellable()
    {
        await using var server = await TestServerFixture.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Handler = async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new CallToolResult { StructuredContent = JsonSerializer.SerializeToElement(true) };
        };
        var adapter = new McpToolAdapter(new("sample-mcp", server.Endpoint));
        try
        {
            var tool = Assert.Single(await adapter.GetToolsAsync(default));
            var execution = adapter.ExecuteAsync(tool, new("one", tool.ModelName, JsonSerializer.SerializeToElement(new { })), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var waitingCancellation = new CancellationTokenSource();
            var discovery = adapter.GetToolsAsync(waitingCancellation.Token);
            Assert.False(discovery.IsCompleted);
            waitingCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
            Assert.Equal(1, server.Discoveries);
            var disposal = adapter.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            release.TrySetResult();
            Assert.True((await execution.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            var requests = server.Methods.Count;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.GetToolsAsync(default));
            Assert.Equal(requests, server.Methods.Count);
        }
        finally { release.TrySetResult(); await adapter.DisposeAsync(); }
    }

    [Fact]
    public async Task Concurrent_initial_discoveries_reuse_one_initialized_client()
    {
        await using var server = await TestServerFixture.StartAsync();
        await using var adapter = new McpToolAdapter(new("sample-mcp", server.Endpoint));
        var results = await Task.WhenAll(adapter.GetToolsAsync(default), adapter.GetToolsAsync(default));
        Assert.All(results, r => Assert.Single(r));
        Assert.Equal(2, server.Discoveries);
        Assert.Single(server.Methods, m => m == "server/discover");
    }
}
