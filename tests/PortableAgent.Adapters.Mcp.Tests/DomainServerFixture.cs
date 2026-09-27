using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using PortableAgent.Sample.FlightBookingMcpServer;
using PortableAgent.Sample.PetBoardingMcpServer;

namespace PortableAgent.Adapters.Mcp.Tests;

/// <summary>Real sample business handlers on Kestrel. Only the test discovery catalog is mutable.</summary>
internal sealed class DomainServerFixture : IAsyncDisposable
{
    private WebApplication _app = null!;
    public Uri Endpoint { get; private set; } = null!;
    public PetBoardingState Pet { get; } = new();
    public FlightBookingState Flight { get; } = new();
    public IList<Tool> Catalog { get; private set; } = null!;
    public ConcurrentQueue<JsonElement> Requests { get; } = new();
    // Test-only injection; production sample handlers and catalogs stay static.
    public Func<CallToolRequestParams, CallToolResult>? CallOverride { get; set; }
    public int Discoveries => Requests.Count(r => r.GetProperty("method").GetString() == "tools/list");
    public JsonElement[] Calls(string toolName) => Requests.Where(r =>
        r.GetProperty("method").GetString() == "tools/call"
        && r.GetProperty("params").GetProperty("name").GetString() == toolName).ToArray();

    public static async Task<DomainServerFixture> StartAsync(bool pet = false)
    {
        var fixture = new DomainServerFixture();
        var petTools = new PetBoardingTools(fixture.Pet);
        var flightTools = new FlightBookingTools(fixture.Flight);
        fixture.Catalog = (pet ? petTools.List() : flightTools.List()).Tools;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(fixture.Pet);
        builder.Services.AddSingleton(fixture.Flight);
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = fixture.Catalog }))
            .WithCallToolHandler((request, _) => ValueTask.FromResult(fixture.CallOverride is { } custom
                ? custom(request.Params!) : pet ? petTools.Call(request.Params!) : flightTools.Call(request.Params!)));
        fixture._app = builder.Build();
        fixture._app.Use(async (context, next) =>
        {
            if (context.Request.Method == "POST")
            {
                context.Request.EnableBuffering();
                var request = await JsonSerializer.DeserializeAsync<JsonRpcRequest>(context.Request.Body,
                    cancellationToken: context.RequestAborted);
                context.Request.Body.Position = 0;
                if (request is not null) fixture.Requests.Enqueue(JsonSerializer.SerializeToElement(request));
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
