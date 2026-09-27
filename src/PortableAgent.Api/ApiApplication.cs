using System.Text.Json.Serialization;
using PortableAgent.Api.Endpoints;
using PortableAgent.Api.Hosting;
using PortableAgent.Api.Streaming;
using PortableAgent.Core.Execution;
using PortableAgent.Persistence.Sqlite;

namespace PortableAgent.Api;

public static class ApiApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5100");
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
        builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton(services => new ApiDatabase(Path.GetFullPath(
            services.GetRequiredService<IConfiguration>()["DatabasePath"] ?? "portable-agent.db")));
        builder.Services.AddSingleton(services => new SqliteRunStateStore(services.GetRequiredService<ApiDatabase>().Path));
        builder.Services.AddSingleton<RunOperationSignals>();
        builder.Services.AddSingleton<RunEventHub>();
        builder.Services.AddSingleton<RunEventStreamSettings>();
        builder.Services.AddSingleton<RunEventStream>();
        builder.Services.AddSingleton<IRunStateStore>(services => new AcknowledgingRunStateStore(
            services.GetRequiredService<SqliteRunStateStore>(), services.GetRequiredService<RunOperationSignals>()));
        builder.Services.AddSingleton(ApiRuntimeConfiguration.CreateRegistry);
        builder.Services.AddSingleton<RunExecutionCoordinator>();
        builder.Services.AddHostedService<DatabaseInitialization>();
        builder.Services.AddHostedService(services => services.GetRequiredService<RunExecutionCoordinator>());
        configure?.Invoke(builder);
        var app = builder.Build();
        app.Lifetime.ApplicationStopping.Register(app.Services.GetRequiredService<RunEventHub>().Stop);
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            { /* The request ended; any admitted operation remains owned by the Coordinator. */ }
            catch (Exception exception) when (context.Response.HasStarted)
            {
                app.Logger.LogWarning(exception, "Response ended after headers were sent");
                context.Abort();
            }
            catch (ApiProblem problem) { await problem.Result().ExecuteAsync(context); }
            catch (BadHttpRequestException)
            { await new ApiProblem(400, "invalid_request", "The request body or parameters are invalid.").Result().ExecuteAsync(context); }
            catch (Exception exception)
            {
                app.Logger.LogError(exception, "API request failed");
                await new ApiProblem(500, "internal_error", "The request could not be completed.").Result().ExecuteAsync(context);
            }
        });
        app.MapAgentEndpoints();
        app.MapRunEndpoints();
        app.MapRunEventEndpoints();
        return app;
    }
}
