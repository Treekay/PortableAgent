using PortableAgent.Adapters.Mcp;
using PortableAgent.Api.Streaming;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Models;
using PortableAgent.Core.Policies;
using PortableAgent.Core.Tools;
using PortableAgent.Infrastructure.Models;
using PortableAgent.Infrastructure.Policies;
using PortableAgent.Persistence.Sqlite;

namespace PortableAgent.Api.Hosting;

public sealed record ApiDatabase(string Path);

public sealed class DatabaseInitialization(ApiDatabase database, ILogger<DatabaseInitialization> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await SqliteRunStateStore.InitializeAsync(database.Path, ct);
        logger.LogInformation("Runtime database: {DatabasePath}", database.Path);
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public static class ApiRuntimeConfiguration
{
    public static AgentRuntimeRegistry CreateRegistry(IServiceProvider services)
    {
        var config = services.GetRequiredService<IConfiguration>();
        var store = services.GetRequiredService<IRunStateStore>();
        var eventHub = services.GetRequiredService<RunEventHub>();
        AgentRuntimeRegistration Create(bool pet)
        {
            var agentId = pet ? "pet" : "flight";
            var source = pet ? "pet-mcp" : "flight-mcp";
            var definition = pet ? "pet-mcp-demo-v1" : "flight-mcp-demo-v1";
            var endpoint = config[$"Agents:{agentId}:Endpoint"] ?? (pet ? "http://localhost:5102/mcp" : "http://localhost:5103/mcp");
            var adapter = new McpToolAdapter(new(source, new Uri(endpoint)));
            IModelProvider model = pet ? new PetBoardingScriptedModelProvider() : new FlightBookingScriptedModelProvider();
            var policy = new InMemoryPolicyEvaluator(new Dictionary<ToolId, PolicyDecision>
            {
                [new(source, pet ? "get_care_records" : "get_booking")] = new(PolicyOutcome.Allow),
                [new(source, pet ? "create_staff_task" : "cancel_booking")] = pet
                    ? new(PolicyOutcome.Deny, Reason: "Staff task creation is disabled in this demo.")
                    : new(PolicyOutcome.RequireApproval, "confirm-flight-cancellation-v1", "Confirm this exact remote cancellation.")
            });
            return new(agentId, pet ? "Pet Boarding" : "Flight Booking", definition,
                new AgentRunner(definition, model, adapter, adapter, new(), eventSink: eventHub, policyEvaluator: policy, runStore: store), adapter);
        }
        return new([Create(true), Create(false)]);
    }
}
