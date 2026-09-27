using System.Text.Json;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Core.Models;
using PortableAgent.Core.Policies;
using PortableAgent.Core.Tools;
using PortableAgent.Infrastructure.Tools.FlightBooking;
using PortableAgent.Persistence.Sqlite.Serialization;

namespace PortableAgent.Persistence.Tests;

public sealed class RestartTests
{
    [Theory]
    [InlineData(ApprovalChoice.Approve, ToolResultDisposition.Executed, 1, "Booking NZ123 has been cancelled.")]
    [InlineData(ApprovalChoice.Reject, ToolResultDisposition.RejectedByUser, 0, "Understood. I did not cancel the booking.")]
    public async Task New_composition_resumes_same_run_with_frozen_call_and_continuous_history(
        ApprovalChoice choice, ToolResultDisposition disposition, int callCount, string text)
    {
        await using var db = await TestDatabase.CreateAsync();
        // No reference to the original runner, model, executor or store is kept.
        var paused = await new Composition(db.Store()).PauseAsync();
        var before = (await db.Store().GetAsync(paused.RunId, default))!;
        var fresh = new Composition(db.Store(), limits: new(1, 0)); // persisted limits, not new defaults
        var submitted = await fresh.Runner.SubmitApprovalAsync(Composition.Command(paused, choice));
        Assert.Equal(ApprovalSubmissionStatus.Accepted, submitted.Status);
        Assert.Equal(RunStatus.Completed, submitted.RunResult!.Status);
        Assert.Equal(paused.RunId, submitted.RunResult.RunId);
        Assert.Equal(text, submitted.RunResult.FinalText);
        Assert.Equal(callCount, fresh.Executor.Calls.Count);
        if (callCount == 1)
        {
            var call = Assert.Single(fresh.Executor.Calls);
            Assert.Equal(paused.PendingApproval!.ToolCall.CallId, call.CallId);
            Assert.Equal(paused.PendingApproval.ToolCall.ToolName, call.ToolName);
            Assert.True(JsonElement.DeepEquals(paused.PendingApproval.ToolCall.Arguments, call.Arguments));
        }
        Assert.Equal(disposition, Assert.Single(fresh.Model.Requests).Messages[^1].ToolResult!.Disposition);
        var saved = (await db.Store().GetAsync(paused.RunId, default))!;
        Assert.Equal(RunLifecycleState.Completed, saved.Lifecycle);
        Assert.Equal(2, saved.ModelTurns);
        Assert.Equal(callCount, saved.ToolCalls);
        Assert.Equal(before.Limits, saved.Limits);
        Assert.Equal(before.UsedCallIds, saved.UsedCallIds);
        Assert.Null(saved.PendingApproval);
        Assert.Single(saved.ResolvedApprovals);
        var events = await db.Store().ReadEventsAfterAsync(paused.RunId, 0, 1000, default);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(x => (long)x), events.Select(e => e.Sequence));
        Assert.All(events, e => Assert.Equal(paused.RunId, e.RunId));
        Assert.Single(events, e => e.EventType == ExecutionEventType.RunStarted);
        Assert.Equal(saved.LastSequence, events[^1].Sequence);
        Assert.Equal(ExecutionEventType.RunCompleted, events[^1].EventType);
        var page = await db.Store().ReadEventsAfterAsync(paused.RunId, before.LastSequence, 2, default);
        Assert.Equal(new[] { ExecutionEventType.ApprovalResolved, ExecutionEventType.RunResumed }, page.Select(e => e.EventType));
        Assert.Equal(events.Skip((int)before.LastSequence).Take(2).Select(e => e.EventId), page.Select(e => e.EventId));
        Assert.DoesNotContain(events, e => e.Payload.GetRawText().Contains("NZ123", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Payload.GetRawText().Contains("Cancel my booking", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("other-definition")]
    [InlineData("FLIGHT-DEMO-V1")]
    public async Task Wrong_definition_is_case_sensitive_conflict_without_mutation(string definition)
    {
        await using var db = await TestDatabase.CreateAsync();
        var paused = await new Composition(db.Store()).PauseAsync();
        var before = RunStateSerializer.Serialize((await db.Store().GetAsync(paused.RunId, default))!);
        var events = await db.Store().ReadEventsAfterAsync(paused.RunId, 0, 1000, default);
        var fresh = new Composition(db.Store(), definition);
        Assert.Equal(ApprovalSubmissionStatus.Conflict,
            (await fresh.Runner.SubmitApprovalAsync(Composition.Command(paused))).Status);
        Assert.Empty(fresh.Executor.Calls);
        Assert.Empty(fresh.Model.Requests);
        Assert.Equal(before, RunStateSerializer.Serialize((await db.Store().GetAsync(paused.RunId, default))!));
        Assert.Empty(await db.Store().ReadEventsAfterAsync(paused.RunId, events[^1].Sequence, 1000, default));
    }

    [Fact]
    public async Task Independent_stores_racing_same_version_have_one_winner()
    {
        await using var db = await TestDatabase.CreateAsync();
        var paused = await new Composition(db.Store()).PauseAsync();
        using var barrier = new Barrier(2);
        var first = new Composition(new ReadBarrierStore(db.Store(), barrier));
        var second = new Composition(new ReadBarrierStore(db.Store(), barrier));
        var results = await Task.WhenAll(Task.Run(() => first.Runner.SubmitApprovalAsync(Composition.Command(paused))),
            Task.Run(() => second.Runner.SubmitApprovalAsync(Composition.Command(paused)))).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(results, r => r.Status == ApprovalSubmissionStatus.Accepted);
        Assert.Single(results, r => r.Status == ApprovalSubmissionStatus.Conflict);
        Assert.Equal(RunStatus.Completed, results.Single(r => r.RunResult is not null).RunResult!.Status);
        Assert.Equal(1, first.Executor.Calls.Count + second.Executor.Calls.Count);
        var events = await db.Store().ReadEventsAfterAsync(paused.RunId, 0, 1000, default);
        Assert.Single(events, e => e.EventType == ExecutionEventType.ApprovalResolved);
        Assert.Single(events, e => e.EventType == ExecutionEventType.RunResumed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("identity")]
    [InlineData("name")]
    [InlineData("schema")]
    public async Task Changed_tool_after_restart_fails_closed(string change)
    {
        await using var db = await TestDatabase.CreateAsync();
        var paused = await new Composition(db.Store()).PauseAsync();
        var original = paused.PendingApproval!.ToolDefinition;
        var catalog = change switch
        {
            "missing" => Array.Empty<ToolDefinition>(),
            "identity" => [original with { Id = new("other", original.Id.Name) }],
            "name" => [original with { ModelName = "renamed" }],
            _ => [original with { InputSchema = JsonSerializer.SerializeToElement(new { type = "string" }) }]
        };
        var fresh = new Composition(db.Store(), tools: new Catalog(catalog));
        var result = await fresh.Runner.SubmitApprovalAsync(Composition.Command(paused));
        Assert.Equal(RunStatus.Failed, result.RunResult!.Status);
        Assert.Contains("contract", result.RunResult.Error!);
        Assert.Empty(fresh.Executor.Calls);
    }

    [Theory]
    [InlineData(PolicyOutcome.Allow, false, 1)]
    [InlineData(PolicyOutcome.Deny, false, 0)]
    [InlineData(PolicyOutcome.RequireApproval, true, 0)]
    public async Task Changed_policy_after_restart_is_re_evaluated(PolicyOutcome outcome, bool repause, int executions)
    {
        await using var db = await TestDatabase.CreateAsync();
        var paused = await new Composition(db.Store()).PauseAsync();
        var fresh = new Composition(db.Store(), policy: new(outcome, "changed-policy"));
        var result = (await fresh.Runner.SubmitApprovalAsync(Composition.Command(paused))).RunResult!;
        Assert.Equal(repause ? RunStatus.AwaitingApproval : RunStatus.Completed, result.Status);
        Assert.Equal(executions, fresh.Executor.Calls.Count);
        if (repause)
        {
            Assert.NotEqual(paused.PendingApproval!.ApprovalId, result.PendingApproval!.ApprovalId);
            Assert.True(JsonElement.DeepEquals(paused.PendingApproval.ToolCall.Arguments, result.PendingApproval.ToolCall.Arguments));
            var third = new Composition(db.Store(), policy: new(outcome, "changed-policy"));
            Assert.Equal(RunStatus.Completed, (await third.Runner.SubmitApprovalAsync(Composition.Command(result))).RunResult!.Status);
            Assert.Single(third.Executor.Calls);
        }
        if (outcome == PolicyOutcome.Deny)
            Assert.Equal(ToolResultDisposition.DeniedByPolicy, fresh.Model.Requests[0].Messages[^1].ToolResult!.Disposition);
    }

    [Theory]
    [InlineData("model", RunStatus.LimitReached)]
    [InlineData("tools", RunStatus.LimitReached)]
    [InlineData("call-id", RunStatus.Failed)]
    public async Task Saved_budgets_and_call_ids_remain_effective_after_restart(string mode, RunStatus expected)
    {
        await using var db = await TestDatabase.CreateAsync();
        var paused = await new Composition(db.Store(), limits: new(mode == "model" ? 1 : 4, mode == "tools" ? 1 : 4)).PauseAsync();
        var fresh = new Composition(db.Store(), limits: new(100, 100));
        fresh.Model.Script = _ => new(null,
            [paused.PendingApproval!.ToolCall with { CallId = mode == "call-id" ? paused.PendingApproval.ToolCall.CallId : "next" }],
            ModelFinishReason.ToolCalls);
        var result = (await fresh.Runner.SubmitApprovalAsync(Composition.Command(paused))).RunResult!;
        Assert.Equal(expected, result.Status);
        Assert.Single(fresh.Executor.Calls);
        Assert.Equal(mode == "model" ? 0 : 1, fresh.Model.Requests.Count);
    }

    [Fact]
    public async Task Every_live_event_is_already_persisted_even_when_observer_throws()
    {
        await using var db = await TestDatabase.CreateAsync();
        var observed = new List<Guid>();
        var sink = new EventSink(async e =>
        {
            var saved = await db.Store().ReadEventsAfterAsync(e.RunId, e.Sequence - 1, 1, default);
            // Record only verified events: an assertion thrown in a safe sink would otherwise be swallowed.
            if (saved.Count == 1 && saved[0].EventId == e.EventId) observed.Add(e.EventId);
            throw new InvalidOperationException("live observer failed");
        });
        var paused = await new Composition(db.Store(), sink: sink).PauseAsync();
        var fresh = new Composition(db.Store(), sink: sink);
        Assert.Equal(RunStatus.Completed, (await fresh.Runner.SubmitApprovalAsync(Composition.Command(paused))).RunResult!.Status);
        var events = await db.Store().ReadEventsAfterAsync(paused.RunId, 0, 1000, default);
        Assert.Equal(events.Select(e => e.EventId), observed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Event_read_limit_is_bounded(int limit)
    {
        await using var db = await TestDatabase.CreateAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Store().ReadEventsAfterAsync(Guid.NewGuid(), 0, limit, default).AsTask());
    }

    [Fact]
    public async Task Rejection_continuation_uses_current_catalog_instead_of_persisted_capabilities()
    {
        await using var db = await TestDatabase.CreateAsync();
        var paused = await new Composition(db.Store()).PauseAsync();
        var fresh = new Composition(db.Store(), tools: new Catalog([]));
        fresh.Model.Script = _ => new(null, [paused.PendingApproval!.ToolCall with { CallId = "new-call" }], ModelFinishReason.ToolCalls);
        var result = (await fresh.Runner.SubmitApprovalAsync(Composition.Command(paused, ApprovalChoice.Reject))).RunResult!;
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Empty(fresh.Model.Requests[0].Tools);
        Assert.Contains("Unknown tool", result.Error!);
        Assert.Empty(fresh.Executor.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Definition_id_is_required(string value)
    {
        await using var db = await TestDatabase.CreateAsync();
        Assert.Throws<ArgumentException>(() => new Composition(db.Store(), value));
    }
}
