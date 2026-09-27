using PortableAgent.Api.Hosting;

namespace PortableAgent.Api.Endpoints;

public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app) =>
        app.MapGet("/api/agents", (AgentRuntimeRegistry registry) => Results.Ok(registry.List()));
}
