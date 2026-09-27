using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PortableAgent.Api.Contracts;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;

namespace PortableAgent.Api.Tests;

public sealed class CommandQueryTests
{
    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.DoesNotContain("SECRET", body.GetRawText());
        Assert.False(body.TryGetProperty("stackTrace", out _));
    }

    [Fact]
    public async Task Agents_expose_only_public_names_and_SSE_is_not_implemented()
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db);
        var agents = await host.Client.GetFromJsonAsync<JsonElement>("/api/agents");
        Assert.Equal(new[] { "pet", "flight" }, agents.EnumerateArray().Select(a => a.GetProperty("id").GetString()));
        Assert.All(agents.EnumerateArray(), a => Assert.Equal(new[] { "id", "name" }, a.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/runs/{Guid.NewGuid()}/events")).StatusCode);
    }

    [Fact]
    public async Task Start_ack_waits_for_commit_but_not_blocked_model_and_uses_trusted_id()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { BlockModel = true };
        var control = new StoreControl { HoldStart = true };
        await using var host = await ApiTestHost.StartAsync(db, runtime, control);
        var post = host.Client.PostAsJsonAsync("/api/runs", new { agentId = "test", message = "execute" });
        await control.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(post.IsCompleted);
        Assert.Equal(0, await db.CountAsync());
        control.StartRelease.TrySetResult();
        var response = await post.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<RunAccepted>())!;
        await runtime.ModelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(runtime.ModelRelease.Task.IsCompleted);
        var running = await host.GetAsync(accepted.RunId);
        Assert.Equal("Running", running.Status);
        Assert.True(running.IsActive);
        Assert.Equal(0, running.ModelTurns); // Durable initial snapshot, not live progress.
        Assert.Equal(1, running.SnapshotSequence);
        var state = (await db.Store.GetAsync(accepted.RunId, default))!;
        Assert.Equal(control.RunId, state.RunId);
        var events = await db.Store.ReadEventsAfterAsync(accepted.RunId, 0, 100, default);
        Assert.Equal(ExecutionEventType.RunStarted, events[0].EventType);
        Assert.All(events, e => Assert.Equal(accepted.RunId, e.RunId));
        Assert.True(events[^1].Sequence > running.SnapshotSequence);
        runtime.ModelRelease.TrySetResult();
        var completed = await host.WaitAsync(accepted.RunId, "Completed");
        Assert.Equal("Finished.", completed.FinalText);
        Assert.Equal(2, completed.ModelTurns);
        Assert.Equal(1, completed.ToolCalls);
        Assert.Null(completed.Failure);
    }

    [Fact]
    public async Task Failed_initial_creation_never_returns_accepted_or_leaks_error()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime();
        await using var host = await ApiTestHost.StartAsync(db, runtime, new() { FailStart = true });
        await ProblemAsync(await host.Client.PostAsJsonAsync("/api/runs", new { agentId = "test", message = "execute" }), HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal(0, await db.CountAsync());
        Assert.False(runtime.ModelEntered.Task.IsCompleted);
    }

    [Theory]
    [InlineData("{\"agentId\":\"test\",\"message\":\"execute\",\"runId\":\"chosen\"}")]
    [InlineData("{\"agentId\":\"test\",\"message\":\"execute\",\"runtimeDefinitionId\":\"chosen\"}")]
    [InlineData("{\"agentId\":\"test\",\"message\":\"execute\",\"endpoint\":\"http://evil\"}")]
    [InlineData("{\"agentId\":\"test\",\"message\":\" \"}")]
    [InlineData("{\"agentId\":\" \",\"message\":\"execute\"}")]
    [InlineData("{}")]
    [InlineData("not json")]
    public async Task Invalid_start_json_is_rejected_before_any_run(string json)
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db, new TestRuntime());
        await ProblemAsync(await host.Client.PostAsync("/api/runs", new StringContent(json, Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, await db.CountAsync());
    }

    [Fact]
    public async Task Unknown_agent_and_oversized_message_create_no_run()
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db, new TestRuntime());
        await ProblemAsync(await host.Client.PostAsJsonAsync("/api/runs", new { agentId = "missing", message = "execute" }), HttpStatusCode.NotFound, "agent_not_found");
        await ProblemAsync(await host.Client.PostAsJsonAsync("/api/runs", new { agentId = "test", message = new string('x', 8001) }), HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, await db.CountAsync());
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Approval_or_rejection_is_claimed_durably_before_response(string decision)
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { Approval = true, BlockTool = true };
        var control = new StoreControl { HoldClaim = true };
        await using var host = await ApiTestHost.StartAsync(db, runtime, control);
        var id = await host.StartRunAsync();
        var paused = await host.WaitAsync(id, "AwaitingApproval");
        var pending = paused.PendingApproval!;
        Assert.Equal("write", pending.ToolName);
        Assert.Equal("test-source", pending.ToolId.SourceId);
        Assert.Equal("frozen", pending.Arguments.GetProperty("value").GetString());
        Assert.Equal("Confirm frozen action.", pending.PolicyReason);
        Assert.Empty(runtime.Calls);
        var command = host.ApproveAsync(id, pending.ApprovalId, decision);
        await control.ClaimEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(command.IsCompleted);
        Assert.Equal(RunLifecycleState.AwaitingApproval, (await db.Store.GetAsync(id, default))!.Lifecycle);
        control.ClaimRelease.TrySetResult();
        var response = await command.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<ApprovalAccepted>())!;
        Assert.Equal(id, accepted.RunId);
        Assert.Equal(pending.ApprovalId, accepted.ApprovalId);
        var saved = (await db.Store.GetAsync(id, default))!;
        Assert.Equal(decision == "approve" ? ApprovalStatus.Approved : ApprovalStatus.Rejected, Assert.Single(saved.ResolvedApprovals).Status);
        if (decision == "approve")
        {
            await runtime.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((await host.GetAsync(id)).IsActive);
            Assert.False(runtime.ToolRelease.Task.IsCompleted);
            await ProblemAsync(await host.ApproveAsync(id, pending.ApprovalId), HttpStatusCode.Conflict, "approval_conflict");
            runtime.ToolRelease.TrySetResult();
        }
        var done = await host.WaitAsync(id, "Completed");
        Assert.Equal(decision == "approve" ? "Finished." : "Rejected.", done.FinalText);
        Assert.Equal(decision == "approve" ? 1 : 0, runtime.Calls.Count);
        await ProblemAsync(await host.ApproveAsync(id, pending.ApprovalId), HttpStatusCode.Conflict, "approval_conflict");
        Assert.Equal(decision == "approve" ? 1 : 0, runtime.Calls.Count);
    }

    [Fact]
    public async Task Failed_approval_claim_returns_error_and_preserves_pending_approval()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { Approval = true };
        await using var host = await ApiTestHost.StartAsync(db, runtime, new() { FailClaim = true });
        var id = await host.StartRunAsync();
        var paused = await host.WaitAsync(id, "AwaitingApproval");
        await ProblemAsync(await host.ApproveAsync(id, paused.PendingApproval!.ApprovalId), HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal(RunLifecycleState.AwaitingApproval, (await db.Store.GetAsync(id, default))!.Lifecycle);
        Assert.Empty(runtime.Calls);
    }

    [Theory]
    [InlineData("{\"decision\":1}")]
    [InlineData("{\"decision\":\"invalid\"}")]
    [InlineData("{\"decision\":\"approve\",\"agentId\":\"other\"}")]
    public async Task Invalid_approval_body_never_claims_approval(string body)
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db, new TestRuntime { Approval = true });
        var id = await host.StartRunAsync();
        var paused = await host.WaitAsync(id, "AwaitingApproval");
        await ProblemAsync(await host.Client.PostAsync($"/api/runs/{id}/approvals/{paused.PendingApproval!.ApprovalId}",
            new StringContent(body, Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(RunLifecycleState.AwaitingApproval, (await db.Store.GetAsync(id, default))!.Lifecycle);
    }

    [Fact]
    public async Task Cancel_only_active_execution_and_never_overwrite_terminal_or_paused_state()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { BlockModel = true };
        await using var host = await ApiTestHost.StartAsync(db, runtime);
        var id = await host.StartRunAsync();
        await runtime.ModelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Accepted, (await host.Client.PostAsync($"/api/runs/{id}/cancel", null)).StatusCode);
        await host.WaitAsync(id, "Cancelled");
        Assert.Empty(runtime.Calls);
        await ProblemAsync(await host.Client.PostAsync($"/api/runs/{id}/cancel", null), HttpStatusCode.Conflict, "run_not_active");
        await ProblemAsync(await host.Client.PostAsync($"/api/runs/{Guid.NewGuid()}/cancel", null), HttpStatusCode.NotFound, "run_not_found");
    }

    [Fact]
    public async Task Aborting_start_http_wait_does_not_cancel_coordinator_execution()
    {
        using var db = new DatabaseFile();
        var control = new StoreControl { HoldStart = true };
        var runtime = new TestRuntime { BlockModel = true };
        await using var host = await ApiTestHost.StartAsync(db, runtime, control);
        using var request = new CancellationTokenSource();
        var post = host.Client.PostAsJsonAsync("/api/runs", new { agentId = "test", message = "execute" }, request.Token);
        await control.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await post);
        control.StartRelease.TrySetResult();
        await runtime.ModelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await host.GetAsync(control.RunId)).IsActive);
        runtime.ModelRelease.TrySetResult();
        await host.WaitAsync(control.RunId, "Completed");
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task Aborting_approval_http_wait_does_not_cancel_resumed_execution()
    {
        using var db = new DatabaseFile();
        var control = new StoreControl { HoldClaim = true };
        var runtime = new TestRuntime { Approval = true, BlockTool = true };
        await using var host = await ApiTestHost.StartAsync(db, runtime, control);
        var id = await host.StartRunAsync();
        var paused = await host.WaitAsync(id, "AwaitingApproval");
        using var request = new CancellationTokenSource();
        var post = host.ApproveAsync(id, paused.PendingApproval!.ApprovalId, ct: request.Token);
        await control.ClaimEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await post);
        control.ClaimRelease.TrySetResult();
        await runtime.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await host.GetAsync(id)).IsActive);
        runtime.ToolRelease.TrySetResult();
        await host.WaitAsync(id, "Completed");
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task Failed_query_is_generic_and_stable_after_host_recreation()
    {
        using var db = new DatabaseFile();
        Guid id;
        await using (var first = await ApiTestHost.StartAsync(db, new TestRuntime { FailModel = true }))
        {
            id = await first.StartRunAsync();
            var run = await first.WaitAsync(id, "Failed");
            Assert.Equal(new PublicFailure("run_failed", "Run execution did not complete."), run.Failure);
            Assert.Null(run.FinalText);
            Assert.DoesNotContain("SECRET", await first.Client.GetStringAsync($"/api/runs/{id}"));
        }
        await using var second = await ApiTestHost.StartAsync(db, new TestRuntime());
        var saved = await second.GetAsync(id);
        Assert.Equal("Failed", saved.Status);
        Assert.Equal("run_failed", saved.Failure!.Code);
        Assert.False(saved.IsActive);
    }

    [Fact]
    public async Task Restart_queries_terminal_and_resumes_approval_but_leaves_orphan_running_inactive()
    {
        using var db = new DatabaseFile();
        Guid completedId, pendingId;
        Guid orphanId = Guid.NewGuid();
        Guid approvalId;
        await using (var first = await ApiTestHost.StartAsync(db, new TestRuntime { Approval = true }))
        {
            completedId = await first.StartRunAsync();
            var firstPause = await first.WaitAsync(completedId, "AwaitingApproval");
            Assert.Equal(HttpStatusCode.Accepted, (await first.ApproveAsync(completedId, firstPause.PendingApproval!.ApprovalId)).StatusCode);
            await first.WaitAsync(completedId, "Completed");
            pendingId = await first.StartRunAsync();
            approvalId = (await first.WaitAsync(pendingId, "AwaitingApproval")).PendingApproval!.ApprovalId;
            await db.Store.CreateAsync(new() { RunId = orphanId, RuntimeDefinitionId = "test-v1", Limits = new(), LastSequence = 1 },
                [new(Guid.NewGuid(), orphanId, 1, DateTimeOffset.UtcNow, ExecutionEventType.RunStarted, JsonSerializer.SerializeToElement(new { }))], default);
        }
        var runtime = new TestRuntime { Approval = true };
        await using var second = await ApiTestHost.StartAsync(db, runtime);
        Assert.Equal("Finished.", (await second.GetAsync(completedId)).FinalText);
        Assert.Equal(approvalId, (await second.GetAsync(pendingId)).PendingApproval!.ApprovalId);
        await ProblemAsync(await second.Client.PostAsync($"/api/runs/{pendingId}/cancel", null), HttpStatusCode.Conflict, "run_not_active");
        var orphan = await second.GetAsync(orphanId);
        Assert.Equal("Running", orphan.Status);
        Assert.False(orphan.IsActive);
        await ProblemAsync(await second.Client.PostAsync($"/api/runs/{orphanId}/cancel", null), HttpStatusCode.Conflict, "run_not_active");
        Assert.Equal(HttpStatusCode.Accepted, (await second.ApproveAsync(pendingId, approvalId)).StatusCode);
        await second.WaitAsync(pendingId, "Completed");
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task Missing_compatible_registration_does_not_substitute_another_runtime()
    {
        using var db = new DatabaseFile();
        Guid id, approval;
        await using (var first = await ApiTestHost.StartAsync(db, new TestRuntime { Approval = true }))
        {
            id = await first.StartRunAsync();
            approval = (await first.WaitAsync(id, "AwaitingApproval")).PendingApproval!.ApprovalId;
        }
        await using var second = await ApiTestHost.StartAsync(db, new TestRuntime { DefinitionId = "test-v2", Approval = true });
        Assert.Null((await second.GetAsync(id)).AgentId);
        await ProblemAsync(await second.ApproveAsync(id, approval), HttpStatusCode.Conflict, "runtime_unavailable");
    }

    [Fact]
    public async Task Unknown_ids_wrong_approval_and_bad_guid_have_consistent_problem_responses()
    {
        using var db = new DatabaseFile();
        await using var host = await ApiTestHost.StartAsync(db, new TestRuntime { Approval = true });
        await ProblemAsync(await host.Client.GetAsync("/api/runs/not-a-guid"), HttpStatusCode.BadRequest, "invalid_request");
        await ProblemAsync(await host.Client.GetAsync($"/api/runs/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "run_not_found");
        await ProblemAsync(await host.ApproveAsync(Guid.NewGuid(), Guid.NewGuid()), HttpStatusCode.NotFound, "run_not_found");
        var id = await host.StartRunAsync();
        await host.WaitAsync(id, "AwaitingApproval");
        await ProblemAsync(await host.ApproveAsync(id, Guid.NewGuid()), HttpStatusCode.Conflict, "approval_conflict");
    }

    [Fact]
    public async Task Shutdown_cancels_active_execution_stops_admission_and_disposes_resources_once()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { BlockModel = true };
        var host = await ApiTestHost.StartAsync(db, runtime);
        var id = await host.StartRunAsync();
        await runtime.ModelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Exercise the host's stopping gate before the HTTP listener is stopped.
        host.App.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        var registration = host.App.Services.GetRequiredService<Hosting.AgentRuntimeRegistry>().ByAgent("test")!;
        var stopped = Assert.Throws<Endpoints.ApiProblem>(() => host.Coordinator.Start(registration, "execute"));
        Assert.Equal(503, stopped.Status);
        await host.DisposeAsync();
        Assert.Equal(RunLifecycleState.Cancelled, (await db.Store.GetAsync(id, default))!.Lifecycle);
        Assert.Equal(1, runtime.DisposeCount);
        await registration.DisposeAsync();
        Assert.Equal(1, runtime.DisposeCount);
    }
}
