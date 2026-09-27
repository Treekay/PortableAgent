using PortableAgent.Api.Contracts;
using PortableAgent.Api.Hosting;
using PortableAgent.Core.Execution;

namespace PortableAgent.Api.Endpoints;

public static class RunEndpoints
{
    public static void MapRunEndpoints(this WebApplication app)
    {
        app.MapGet("/api/runs/{runId}/tools", async (string runId, IRunStateStore store, CancellationToken requestAborted) =>
            Results.Ok(RunToolsDto.FromState(await GetAsync(ParseId(runId), store, requestAborted))));

        app.MapPost("/api/runs", async (StartRunRequest request, AgentRuntimeRegistry registry,
            RunExecutionCoordinator coordinator, CancellationToken requestAborted) =>
        {
            if (string.IsNullOrWhiteSpace(request.AgentId) || string.IsNullOrWhiteSpace(request.Message)
                || request.Message.Length > 8000)
                throw new ApiProblem(400, "invalid_request", "AgentId and a message of 1–8000 characters are required.");
            var registration = registry.ByAgent(request.AgentId)
                ?? throw new ApiProblem(404, "agent_not_found", "Agent was not found.");
            var operation = coordinator.Start(registration, request.Message);
            await AwaitCommitAsync(operation, approval: false, requestAborted);
            var url = $"/api/runs/{operation.RunId}";
            return Results.Accepted(url, new RunAccepted(operation.RunId, "accepted", url));
        });

        app.MapGet("/api/runs/{runId}", async (string runId, IRunStateStore store,
            AgentRuntimeRegistry registry, RunExecutionCoordinator coordinator, CancellationToken requestAborted) =>
        {
            var state = await GetAsync(ParseId(runId), store, requestAborted);
            return Results.Ok(RunDto.FromState(state, registry.ByDefinition(state.RuntimeDefinitionId)?.AgentId,
                coordinator.IsActive(state.RunId)));
        });

        app.MapPost("/api/runs/{runId}/approvals/{approvalId}", async (string runId, string approvalId,
            ApprovalRequest request, IRunStateStore store, AgentRuntimeRegistry registry,
            RunExecutionCoordinator coordinator, CancellationToken requestAborted) =>
        {
            var id = ParseId(runId);
            var approval = ParseId(approvalId);
            var decision = request.Decision switch
            {
                "approve" => ApprovalChoice.Approve,
                "reject" => ApprovalChoice.Reject,
                _ => throw new ApiProblem(400, "invalid_request", "Decision must be approve or reject.")
            };
            var state = await GetAsync(id, store, requestAborted);
            var registration = registry.ByDefinition(state.RuntimeDefinitionId)
                ?? throw new ApiProblem(409, "runtime_unavailable", "No compatible Runtime is configured for this Run.");
            var operation = coordinator.Resume(registration, new(id, approval, decision));
            await AwaitCommitAsync(operation, approval: true, requestAborted);
            var url = $"/api/runs/{id}";
            return Results.Accepted(url, new ApprovalAccepted(id, approval, "accepted", url));
        });

        app.MapPost("/api/runs/{runId}/cancel", async (string runId, IRunStateStore store,
            RunExecutionCoordinator coordinator, CancellationToken requestAborted) =>
        {
            var id = ParseId(runId);
            var state = await GetAsync(id, store, requestAborted);
            if (state.Lifecycle != RunLifecycleState.Running || !coordinator.Cancel(id))
                throw new ApiProblem(409, "run_not_active", "Run is not actively executing in this host.");
            var url = $"/api/runs/{id}";
            return Results.Accepted(url, new RunAccepted(id, "accepted", url));
        });
    }

    private static Guid ParseId(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id
        : throw new ApiProblem(400, "invalid_request", "A non-empty GUID is required.");

    private static async Task<RunState> GetAsync(Guid id, IRunStateStore store, CancellationToken ct) =>
        await store.GetAsync(id, ct) ?? throw new ApiProblem(404, "run_not_found", "Run was not found.");

    private static async Task AwaitCommitAsync(RunExecutionCoordinator.Operation operation, bool approval, CancellationToken requestAborted)
    {
        // Cancels only the caller's wait. Neither task receives the HTTP cancellation token.
        await Task.WhenAny(operation.Committed, operation.Completion).WaitAsync(requestAborted);
        if (operation.Committed.IsCompletedSuccessfully) return;
        var outcome = await operation.Completion;
        if (approval && outcome == ApprovalSubmissionStatus.NotFound)
            throw new ApiProblem(404, "run_not_found", "Run was not found.");
        if (approval && outcome == ApprovalSubmissionStatus.Conflict)
            throw new ApiProblem(409, "approval_conflict", "Approval does not match a pending operation.");
        throw new ApiProblem(500, "internal_error", "The operation could not be durably acknowledged.");
    }
}
