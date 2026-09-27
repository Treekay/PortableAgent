using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using PortableAgent.Sample.McpServer;

namespace PortableAgent.Adapters.Mcp.Tests;

internal sealed class TestServerFixture : IAsyncDisposable
{
    private WebApplication _app = null!;
    private int _discoveries;
    public Uri Endpoint { get; private set; } = null!;
    public int Discoveries => _discoveries;
    public ConcurrentQueue<CallToolRequestParams> Calls { get; } = new();
    public ConcurrentQueue<string> Methods { get; } = new();
    public IList<Tool> Tools { get; set; } = [Definition()];
    public bool FailDiscovery { get; set; }
    public string? Fault { get; set; }
    public Func<CallToolRequestParams, CancellationToken, ValueTask<CallToolResult>> Handler { get; set; } =
        (_, _) => ValueTask.FromResult(new CallToolResult { StructuredContent = JsonSerializer.SerializeToElement(new { service = "sample", status = "ok" }) });

    public static Tool Definition(string name = "get_server_status") => new()
    {
        Name = name, Description = "Test service status.",
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
    };

    public static async Task<TestServerFixture> StartAsync(bool sampleTools = false)
    {
        var fixture = new TestServerFixture();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var mcp = builder.Services.AddMcpServer().WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless);
        if (sampleTools) mcp.WithTools<ServerStatusTool>();
        else
        {
            mcp.WithListToolsHandler((_, _) =>
            {
                Interlocked.Increment(ref fixture._discoveries);
                if (fixture.FailDiscovery) throw new InvalidOperationException("Injected discovery failure.");
                return ValueTask.FromResult(new ListToolsResult { Tools = fixture.Tools });
            });
            mcp.WithCallToolHandler((request, ct) =>
            {
                fixture.Calls.Enqueue(request.Params!);
                return fixture.Handler(request.Params!, ct);
            });
        }
        fixture._app = builder.Build();
        // Test-only request observation and HTTP fault injection, never a replacement MCP implementation.
        fixture._app.Use(async (context, next) =>
        {
            if (context.Request.Method == "POST")
            {
                context.Request.EnableBuffering();
                var request = await JsonSerializer.DeserializeAsync<JsonRpcRequest>(context.Request.Body, cancellationToken: context.RequestAborted);
                context.Request.Body.Position = 0;
                if (request is not null) fixture.Methods.Enqueue(request.Method);
                if (request?.Method == "tools/call" && fixture.Fault is { } fault)
                {
                    context.Response.StatusCode = fault == "http" ? 502 : 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("not a protocol response", context.RequestAborted);
                    return;
                }
            }
            await next(context);
        });
        fixture._app.MapMcp("/mcp");
        await fixture._app.StartAsync();
        var address = fixture._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        fixture.Endpoint = new Uri(address + "/mcp");
        return fixture;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
