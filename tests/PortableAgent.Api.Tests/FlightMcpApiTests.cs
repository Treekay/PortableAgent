using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
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
using PortableAgent.Api.Contracts;
using PortableAgent.Sample.FlightBookingMcpServer;

namespace PortableAgent.Api.Tests;

public sealed class FlightMcpApiTests
{
    [Fact]
    public async Task Flight_approval_is_observed_on_one_continuous_SSE_connection()
    {
        using var db = new DatabaseFile();
        await using var flight = await FlightServer.StartAsync();
        await using var api = await ApiTestHost.StartAsync(db, flightEndpoint: flight.Endpoint);
        var id = await api.StartRunAsync("Cancel my booking.", "flight");
        using var stream = await SseClient.OpenAsync(api.Client, id);
        await stream.UntilAsync("ApprovalRequired");
        var pending = await api.WaitAsync(id, "AwaitingApproval");
        Assert.Equal(0, flight.State.CancelCallCount);
        Assert.Equal(HttpStatusCode.Accepted, (await api.ApproveAsync(id, pending.PendingApproval!.ApprovalId)).StatusCode);
        await stream.ReadToEndAsync();
        Assert.Equal(new[] { "RunStarted", "ToolDiscoveryStarted", "ToolDiscoveryCompleted", "ModelTurnStarted", "ModelTurnCompleted",
            "ToolCallProposed", "PolicyEvaluationStarted", "PolicyEvaluationCompleted", "ApprovalRequired", "ApprovalResolved", "RunResumed",
            "ToolDiscoveryStarted", "ToolDiscoveryCompleted", "PolicyEvaluationStarted", "PolicyEvaluationCompleted", "ToolExecutionStarted",
            "ToolExecutionCompleted", "ModelTurnStarted", "ModelTurnCompleted", "RunCompleted" }, stream.Events.Select(e => e.EventType));
        Assert.Equal(Enumerable.Range(1, 20).Select(n => (long)n), stream.Events.Select(e => e.Sequence));
        Assert.All(stream.Events, e => Assert.Equal(id, e.RunId));
        Assert.Equal("Booking NZ123 has been cancelled.", (await api.WaitAsync(id, "Completed")).FinalText);
        Assert.Equal(1, flight.State.CancelCallCount);
        Assert.Equal(1, flight.State.CancelMutationCount);
        Assert.Equal("cancelled", flight.State.GetBooking("NZ123").Status);
    }

    [Theory]
    [InlineData("approve", 1, "cancelled")]
    [InlineData("reject", 0, "confirmed")]
    public async Task Real_HTTP_API_resumes_same_run_through_external_Flight_MCP(string decision, int calls, string status)
    {
        using var db = new DatabaseFile();
        await using var flight = await FlightServer.StartAsync();
        await using var api = await ApiTestHost.StartAsync(db, flightEndpoint: flight.Endpoint);
        var agents = (await api.Client.GetFromJsonAsync<AgentDto[]>("/api/agents"))!;
        Assert.Equal(new[] { new AgentDto("pet", "Pet Boarding"), new AgentDto("flight", "Flight Booking") }, agents);
        var id = await api.StartRunAsync("Cancel my booking.", "flight");
        var pending = await api.WaitAsync(id, "AwaitingApproval");
        Assert.Equal("flight", pending.AgentId);
        Assert.Equal("cancel_booking", pending.PendingApproval!.ToolName);
        Assert.Equal(new ToolIdentityDto("flight-mcp", "cancel_booking"), pending.PendingApproval.ToolId);
        Assert.Equal("NZ123", pending.PendingApproval.Arguments.GetProperty("bookingId").GetString());
        Assert.Equal(0, flight.State.CancelCallCount);
        Assert.Equal("confirmed", flight.State.GetBooking("NZ123").Status);
        var response = await api.ApproveAsync(id, pending.PendingApproval.ApprovalId, decision);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var ack = (await response.Content.ReadFromJsonAsync<ApprovalAccepted>())!;
        Assert.Equal(id, ack.RunId);
        Assert.Equal(pending.PendingApproval.ApprovalId, ack.ApprovalId);
        var completed = await api.WaitAsync(id, "Completed");
        Assert.Equal(decision == "approve" ? "Booking NZ123 has been cancelled." : "Understood. I did not cancel the booking.", completed.FinalText);
        Assert.Equal(calls, flight.State.CancelCallCount);
        Assert.Equal(calls, flight.State.CancelMutationCount);
        Assert.Equal(status, flight.State.GetBooking("NZ123").Status);
        Assert.Equal(HttpStatusCode.Conflict, (await api.ApproveAsync(id, pending.PendingApproval.ApprovalId, decision)).StatusCode);
        var remoteCalls = flight.Requests.Where(r => r.GetProperty("method").GetString() == "tools/call").ToArray();
        Assert.Equal(calls, remoteCalls.Length);
        Assert.All(remoteCalls, call =>
        {
            var args = call.GetProperty("params").GetProperty("arguments");
            Assert.Equal("bookingId", Assert.Single(args.EnumerateObject()).Name);
            Assert.Equal("NZ123", args.GetProperty("bookingId").GetString());
            Assert.DoesNotContain(id.ToString(), call.GetRawText());
            Assert.DoesNotContain(pending.PendingApproval.ApprovalId.ToString(), call.GetRawText());
        });
        Assert.Equal(2, flight.Requests.Count(r => r.GetProperty("method").GetString() == "tools/list"));
        Assert.All(await db.Store.ReadEventsAfterAsync(id, 0, 100, default), e => Assert.Equal(id, e.RunId));
    }

    // Real sample handlers, official MCP transport and a separate Kestrel listener; no local executor.
    private sealed class FlightServer(WebApplication app, FlightBookingState state, ConcurrentQueue<JsonElement> requests, string endpoint) : IAsyncDisposable
    {
        public FlightBookingState State { get; } = state;
        public ConcurrentQueue<JsonElement> Requests { get; } = requests;
        public string Endpoint { get; } = endpoint;
        public static async Task<FlightServer> StartAsync()
        {
            var state = new FlightBookingState();
            var handlers = new FlightBookingTools(state);
            var requests = new ConcurrentQueue<JsonElement>();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddMcpServer().WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
                .WithListToolsHandler((_, _) => ValueTask.FromResult(handlers.List()))
                .WithCallToolHandler((request, _) => ValueTask.FromResult(handlers.Call(request.Params!)));
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (context.Request.Method == "POST")
                {
                    context.Request.EnableBuffering();
                    using var document = await JsonDocument.ParseAsync(context.Request.Body);
                    requests.Enqueue(document.RootElement.Clone());
                    context.Request.Body.Position = 0;
                }
                await next(context);
            });
            app.MapMcp("/mcp");
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, state, requests, address + "/mcp");
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
