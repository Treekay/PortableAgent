using System.Globalization;
using PortableAgent.Api.Streaming;
using PortableAgent.Core.Execution;

namespace PortableAgent.Api.Endpoints;

public static class RunEventEndpoints
{
    public static void MapRunEventEndpoints(this WebApplication app)
    {
        app.MapGet("/api/runs/{runId}/events", async (string runId, HttpContext context,
            IRunStateStore store, RunEventStream stream) =>
        {
            if (!Guid.TryParse(runId, out var id) || id == Guid.Empty)
                throw new ApiProblem(400, "invalid_request", "A non-empty GUID is required.");
            var header = context.Request.Headers["Last-Event-ID"].ToString();
            var value = !string.IsNullOrEmpty(header) ? header : context.Request.Query["afterSequence"].ToString();
            long cursor = 0;
            if ((!string.IsNullOrEmpty(header) || context.Request.Query.ContainsKey("afterSequence"))
                && (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out cursor) || cursor < 0))
                throw new ApiProblem(400, "invalid_request", "Event cursor must be a nonnegative Int64 integer.");
            var state = await store.GetAsync(id, context.RequestAborted)
                ?? throw new ApiProblem(404, "run_not_found", "Run was not found.");
            if (state.Lifecycle is RunLifecycleState.Completed or RunLifecycleState.Failed
                or RunLifecycleState.Cancelled or RunLifecycleState.LimitReached && cursor >= state.LastSequence)
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            await stream.WriteAsync(context, id, cursor);
        });
    }
}
