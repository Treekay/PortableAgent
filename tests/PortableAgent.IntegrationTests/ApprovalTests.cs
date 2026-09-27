using System.Text.Json;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Core.Models;
using PortableAgent.Core.Policies;
using PortableAgent.Core.Tools;
using PortableAgent.Infrastructure.Execution;
using PortableAgent.Infrastructure.Models;
using PortableAgent.Infrastructure.Policies;
using PortableAgent.Infrastructure.Tools.FlightBooking;
using PortableAgent.Infrastructure.Tools.PetBoarding;

namespace PortableAgent.IntegrationTests;

public sealed class ApprovalTests
{
    private static readonly ToolDefinition Tool = new(new("test", "write"), "write", "Test write",
        JsonSerializer.SerializeToElement(new { type = "object" }));
    private static ToolCall Call(string id = "one") => new(id, "write", JsonSerializer.SerializeToElement(new { value = 7 }));
    private static ModelReply Proposal(params ToolCall[] calls) => new(null, calls, ModelFinishReason.ToolCalls);
    private static ModelReply Final() => new("Finished.", [], ModelFinishReason.Completed);

    [Theory]
    [InlineData(PolicyOutcome.Allow, 1, ToolResultDisposition.Executed)]
    [InlineData(PolicyOutcome.Deny, 0, ToolResultDisposition.DeniedByPolicy)]
    public async Task Allow_and_deny_have_distinct_execution_semantics(
        PolicyOutcome outcome, int executions, ToolResultDisposition disposition)
    {
        var f = new Fixture();
        f.Policy.Current = new(outcome);
        var result = await f.Runner.RunAsync(new("input"));
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(executions, f.Executor.Calls.Count);
        var returned = f.Model.Requests[1].Messages[^1].ToolResult!;
        Assert.Equal("one", returned.CallId);
        Assert.Equal(disposition, returned.Disposition);
        Assert.Equal(executions, f.Events.Items.Count(e => e.EventType == ExecutionEventType.ToolExecutionStarted));
    }

    [Fact]
    public async Task Pause_saves_frozen_state_before_observation_and_releases_execution()
    {
        var f = new Fixture();
        RunState? observed = null;
        f.Events.Observe = async e =>
        {
            if (e.EventType == ExecutionEventType.ApprovalRequired)
                observed = await f.Store.GetAsync(e.RunId, CancellationToken.None);
        };
        var result = await f.PauseAsync();
        Assert.Empty(f.Executor.Calls);
        Assert.Single(f.Model.Requests);
        Assert.NotNull(observed);
        Assert.Equal(RunLifecycleState.AwaitingApproval, observed.Lifecycle);
        Assert.Equal(f.Events.Items[^1].Sequence, observed.LastSequence);
        Assert.Equal(1, observed.ModelTurns);
        Assert.Equal(0, observed.ToolCalls);
        Assert.Equal("one", Assert.Single(observed.UsedCallIds));
        Assert.Equal(MessageRole.Assistant, observed.Conversation[^1].Role);
        Assert.DoesNotContain(f.Events.Items, e => IsTerminal(e.EventType));
        Assert.Equal("policy-v1", result.PendingApproval!.PolicyId);
        Assert.Equal("Confirm operation", result.PendingApproval.PolicyReason);
        Assert.Equal(ApprovalStatus.Pending, result.PendingApproval.Status);
    }

    [Theory]
    [InlineData(ApprovalChoice.Approve, 1, ToolResultDisposition.Executed)]
    [InlineData(ApprovalChoice.Reject, 0, ToolResultDisposition.RejectedByUser)]
    public async Task Resolve_continues_same_run_and_preserves_sequence(
        ApprovalChoice choice, int executions, ToolResultDisposition disposition)
    {
        var f = new Fixture();
        var paused = await f.PauseAsync();
        var pauseSequence = f.Events.Items[^1].Sequence;
        var submitted = await f.Runner.SubmitApprovalAsync(Command(paused, choice));
        Assert.Equal(ApprovalSubmissionStatus.Accepted, submitted.Status);
        Assert.Equal(RunStatus.Completed, submitted.RunResult!.Status);
        Assert.Equal(paused.RunId, submitted.RunResult.RunId);
        Assert.Equal(executions, f.Executor.Calls.Count);
        if (executions == 1)
        {
            var executed = Assert.Single(f.Executor.Calls);
            Assert.Equal(paused.PendingApproval!.ToolCall.CallId, executed.CallId);
            Assert.True(JsonElement.DeepEquals(paused.PendingApproval.ToolCall.Arguments, executed.Arguments));
        }
        Assert.Equal(disposition, f.Model.Requests[1].Messages[^1].ToolResult!.Disposition);
        Assert.Equal(ExecutionEventType.ApprovalResolved, f.Events.Items[(int)pauseSequence].EventType);
        Assert.Equal(Enumerable.Range(1, f.Events.Items.Count).Select(i => (long)i), f.Events.Items.Select(e => e.Sequence));
        Assert.All(f.Events.Items, e => Assert.Equal(paused.RunId, e.RunId));
        Assert.Single(f.Events.Items, e => e.EventType == ExecutionEventType.RunStarted);
        var stored = await f.Store.GetAsync(paused.RunId, CancellationToken.None);
        Assert.Equal(RunLifecycleState.Completed, stored!.Lifecycle);
        Assert.Equal(choice == ApprovalChoice.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected,
            Assert.Single(stored.ResolvedApprovals).Status);
    }

    [Fact]
    public async Task Invalid_and_duplicate_commands_do_not_consume_another_approval()
    {
        var f = new Fixture();
        var paused = await f.PauseAsync();
        var command = Command(paused);
        Assert.Equal(ApprovalSubmissionStatus.NotFound,
            (await f.Runner.SubmitApprovalAsync(command with { RunId = Guid.NewGuid() })).Status);
        Assert.Equal(ApprovalSubmissionStatus.Conflict,
            (await f.Runner.SubmitApprovalAsync(command with { ApprovalId = Guid.NewGuid() })).Status);
        Assert.Empty(f.Executor.Calls);
        Assert.Equal(ApprovalSubmissionStatus.Accepted, (await f.Runner.SubmitApprovalAsync(command)).Status);
        Assert.Equal(ApprovalSubmissionStatus.Conflict, (await f.Runner.SubmitApprovalAsync(command)).Status);
        Assert.Equal(ApprovalSubmissionStatus.Conflict,
            (await f.Runner.SubmitApprovalAsync(command with { Decision = ApprovalChoice.Reject })).Status);
        Assert.Single(f.Executor.Calls);
        Assert.Single(f.Events.Items, e => e.EventType == ExecutionEventType.ApprovalResolved);
    }

    [Fact]
    public async Task Concurrent_approval_commands_read_same_version_but_only_one_wins()
    {
        var store = new StoreWrapper { SynchronizeTwoReads = true };
        var f = new Fixture(store: store);
        var paused = await f.PauseAsync();
        var results = await Task.WhenAll(f.Runner.SubmitApprovalAsync(Command(paused)),
            f.Runner.SubmitApprovalAsync(Command(paused))).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(results, result => result.Status == ApprovalSubmissionStatus.Accepted);
        Assert.Single(results, result => result.Status == ApprovalSubmissionStatus.Conflict);
        Assert.Single(f.Executor.Calls);
        Assert.Single(f.Events.Items, e => e.EventType == ExecutionEventType.ApprovalResolved);
    }

    [Theory]
    [InlineData("model", RunStatus.LimitReached)]
    [InlineData("tools", RunStatus.LimitReached)]
    [InlineData("call-id", RunStatus.Failed)]
    public async Task Budgets_and_used_call_ids_survive_resume(string mode, RunStatus expected)
    {
        var f = new Fixture(new RunLimits(mode == "model" ? 1 : 4, mode == "tools" ? 1 : 4));
        f.Model.Script = request => Proposal(Call(request.Messages.Count == 1 || mode == "call-id" ? "one" : "two"));
        var paused = await f.PauseAsync();
        var result = (await f.Runner.SubmitApprovalAsync(Command(paused))).RunResult!;
        Assert.Equal(expected, result.Status);
        Assert.Single(f.Executor.Calls);
        Assert.Equal(mode == "model" ? 1 : 2, f.Model.Requests.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("name")]
    [InlineData("schema")]
    public async Task Changed_tool_contract_fails_closed(string change)
    {
        var f = new Fixture();
        var paused = await f.PauseAsync();
        f.Tools.Catalog = change switch
        {
            "missing" => [],
            "name" => [Tool with { ModelName = "renamed" }],
            _ => [Tool with { InputSchema = JsonSerializer.SerializeToElement(new { type = "string" }) }]
        };
        var result = (await f.Runner.SubmitApprovalAsync(Command(paused))).RunResult!;
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("contract", result.Error!);
        Assert.Empty(f.Executor.Calls);
    }

    [Theory]
    [InlineData(PolicyOutcome.Allow, 1)]
    [InlineData(PolicyOutcome.Deny, 0)]
    public async Task Current_policy_is_rechecked_after_approval(PolicyOutcome current, int executions)
    {
        var f = new Fixture();
        var paused = await f.PauseAsync();
        f.Policy.Current = new(current, "changed-policy");
        var result = (await f.Runner.SubmitApprovalAsync(Command(paused))).RunResult!;
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(executions, f.Executor.Calls.Count);
        if (current == PolicyOutcome.Deny)
            Assert.Equal(ToolResultDisposition.DeniedByPolicy, f.Model.Requests[1].Messages[^1].ToolResult!.Disposition);
    }

    [Theory]
    [InlineData("policy-v1", "policy-v2", "Confirm operation", true)]
    [InlineData(null, null, "Confirm operation", false)]
    [InlineData(null, null, "Changed anonymous requirement", true)]
    [InlineData(null, "named", "Confirm operation", true)]
    public async Task Changed_policy_identity_requires_a_new_exact_approval(
        string? originalId, string? currentId, string reason, bool repause)
    {
        var f = new Fixture();
        f.Policy.Current = new(PolicyOutcome.RequireApproval, originalId, "Confirm operation");
        var paused = await f.PauseAsync();
        f.Policy.Current = new(PolicyOutcome.RequireApproval, currentId, reason);
        var next = (await f.Runner.SubmitApprovalAsync(Command(paused))).RunResult!;
        Assert.Equal(repause ? RunStatus.AwaitingApproval : RunStatus.Completed, next.Status);
        if (repause)
        {
            Assert.Empty(f.Executor.Calls);
            Assert.Equal(paused.RunId, next.RunId);
            Assert.NotEqual(paused.PendingApproval!.ApprovalId, next.PendingApproval!.ApprovalId);
            Assert.Equal(currentId, next.PendingApproval.PolicyId);
            Assert.True(JsonElement.DeepEquals(paused.PendingApproval.ToolCall.Arguments, next.PendingApproval.ToolCall.Arguments));
            Assert.Equal(ApprovalSubmissionStatus.Conflict, (await f.Runner.SubmitApprovalAsync(Command(paused))).Status);
            Assert.Equal(RunStatus.Completed, (await f.Runner.SubmitApprovalAsync(Command(next))).RunResult!.Status);
        }
        Assert.Single(f.Executor.Calls);
    }

    [Theory]
    [InlineData(PolicyOutcome.Allow, RunStatus.Completed, 2)]
    [InlineData(PolicyOutcome.Deny, RunStatus.Completed, 0)]
    [InlineData(PolicyOutcome.RequireApproval, RunStatus.Failed, 0)]
    public async Task Complete_batch_policy_is_known_before_any_execution(
        PolicyOutcome second, RunStatus status, int executions)
    {
        var f = new Fixture();
        f.Model.Script = request => request.Messages.Count == 1 ? Proposal(Call(), Call("two")) : Final();
        f.Policy.Script = context => new(context.Call.CallId == "one" ? PolicyOutcome.Allow : second);
        f.Executor.BeforeExecute = () => Assert.Equal(2, f.Policy.Evaluations);
        var result = await f.Runner.RunAsync(new("input"));
        Assert.Equal(status, result.Status);
        Assert.Equal(executions, f.Executor.Calls.Count);
        if (second == PolicyOutcome.Deny)
        {
            var results = f.Model.Requests[1].Messages.Where(message => message.Role == MessageRole.Tool).Select(message => message.ToolResult!).ToArray();
            Assert.Equal(new[] { "one", "two" }, results.Select(r => r.CallId));
            Assert.Equal(ToolResultDisposition.NotExecutedDueToBatchPolicy, results[0].Disposition);
            Assert.Equal(ToolResultDisposition.DeniedByPolicy, results[1].Disposition);
        }
        if (second == PolicyOutcome.RequireApproval) Assert.Contains("Multi-call", result.Error!);
    }

    [Fact]
    public async Task Failed_pause_save_does_not_claim_the_run_is_resumable()
    {
        var f = new Fixture(store: new StoreWrapper { FailCreate = true });
        var result = await f.Runner.RunAsync(new("input"));
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Null(result.PendingApproval);
        Assert.DoesNotContain(f.Events.Items, e => e.EventType == ExecutionEventType.ApprovalRequired);
        Assert.Equal(ExecutionEventType.RunFailed, f.Events.Items[^1].EventType);
        Assert.Empty(f.Executor.Calls);
    }

    [Fact]
    public async Task Owned_store_snapshots_and_returned_approvals_cannot_change_frozen_call()
    {
        var f = new Fixture();
        var paused = await f.PauseAsync();
        var snapshot = (await f.Store.GetAsync(paused.RunId, CancellationToken.None))!;
        snapshot.Conversation.Clear();
        snapshot.UsedCallIds.Clear();
        snapshot.ToolCatalog.Clear();
        snapshot.PendingApproval = snapshot.PendingApproval! with
        {
            ToolCall = Call() with { Arguments = JsonSerializer.SerializeToElement(new { value = 99 }) }
        };
        var modifiedView = paused.PendingApproval! with { PolicyId = "forged", ToolCall = snapshot.PendingApproval.ToolCall };
        Assert.NotEqual(paused.PendingApproval.PolicyId, modifiedView.PolicyId);
        var retained = (await f.Store.GetAsync(paused.RunId, CancellationToken.None))!;
        Assert.Equal(2, retained.Conversation.Count);
        Assert.Single(retained.UsedCallIds);
        Assert.Equal(7, retained.PendingApproval!.ToolCall.Arguments.GetProperty("value").GetInt32());
        var result = await f.Runner.SubmitApprovalAsync(Command(paused));
        Assert.Equal(RunStatus.Completed, result.RunResult!.Status);
        Assert.Equal(7, Assert.Single(f.Executor.Calls).Arguments.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Failing_observer_does_not_change_pause_or_resume()
    {
        var f = new Fixture();
        f.Events.Throw = true;
        var paused = await f.PauseAsync();
        Assert.Equal(RunStatus.Completed, (await f.Runner.SubmitApprovalAsync(Command(paused))).RunResult!.Status);
        Assert.Single(f.Executor.Calls);
    }

    [Fact]
    public async Task Cancellation_before_claim_leaves_pending_but_after_claim_never_resets_it()
    {
        using var source = new CancellationTokenSource();
        var store = new StoreWrapper();
        var f = new Fixture(store: store);
        var paused = await f.PauseAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Runner.SubmitApprovalAsync(Command(paused), new(true)));
        Assert.Equal(ApprovalStatus.Pending, (await store.GetAsync(paused.RunId, CancellationToken.None))!.PendingApproval!.Status);
        store.AfterClaim = source.Cancel;
        var resolved = await f.Runner.SubmitApprovalAsync(Command(paused), source.Token);
        Assert.Equal(ApprovalSubmissionStatus.Accepted, resolved.Status);
        Assert.Equal(RunStatus.Cancelled, resolved.RunResult!.Status);
        Assert.Empty(f.Executor.Calls);
        var saved = (await store.GetAsync(paused.RunId, CancellationToken.None))!;
        Assert.Null(saved.PendingApproval);
        Assert.Equal(ApprovalStatus.Approved, Assert.Single(saved.ResolvedApprovals).Status);
        Assert.Equal(ApprovalSubmissionStatus.Conflict, (await f.Runner.SubmitApprovalAsync(Command(paused))).Status);
    }

    [Theory]
    [InlineData(ApprovalChoice.Approve, "Booking NZ123 has been cancelled.", 1)]
    [InlineData(ApprovalChoice.Reject, "Understood. I did not cancel the booking.", 0)]
    public async Task Real_flight_components_understand_approval_and_rejection(ApprovalChoice choice, string text, int calls)
    {
        var executor = new RecordingExecutor(new FlightBookingToolExecutor());
        var runner = new AgentRunner(new FlightBookingScriptedModelProvider(), new FlightBookingToolProvider(), executor,
            new(), policyEvaluator: new InMemoryPolicyEvaluator(new Dictionary<ToolId, PolicyDecision>
            { [new("flight-local", "booking.cancel")] = new(PolicyOutcome.RequireApproval, "flight-confirm") }),
            runStore: new InMemoryRunStateStore());
        var paused = await runner.RunAsync(new("Cancel my booking."));
        var result = await runner.SubmitApprovalAsync(Command(paused, choice));
        Assert.Equal(RunStatus.Completed, result.RunResult!.Status);
        Assert.Equal(text, result.RunResult.FinalText);
        Assert.Equal(calls, executor.Calls);
    }

    [Fact]
    public async Task Real_pet_components_explain_denial_and_explicit_policy_defaults_to_deny()
    {
        var executor = new RecordingExecutor(new PetBoardingToolExecutor());
        var runner = new AgentRunner(new PetBoardingScriptedModelProvider(), new PetBoardingToolProvider(), executor,
            new(), policyEvaluator: new InMemoryPolicyEvaluator(new Dictionary<ToolId, PolicyDecision>()));
        var result = await runner.RunAsync(new("Ask the staff to give Cooper some fresh water."));
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Contains("not created", result.FinalText!);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task Store_copies_on_create_and_replace_and_rejects_stale_versions()
    {
        var store = new InMemoryRunStateStore();
        var original = new RunState { RunId = Guid.NewGuid(), RuntimeId = Guid.NewGuid(), Limits = new() };
        original.Conversation.Add(AgentMessage.User("original"));
        await store.CreateAsync(original, CancellationToken.None);
        original.Conversation.Clear();
        var copy = (await store.GetAsync(original.RunId, CancellationToken.None))!;
        Assert.Single(copy.Conversation);
        copy.Version = 1;
        copy.ModelTurns = 2;
        Assert.True(await store.TryReplaceAsync(copy.RunId, 0, copy, CancellationToken.None));
        copy.Conversation.Clear();
        copy.ModelTurns = 99;
        Assert.False(await store.TryReplaceAsync(copy.RunId, 0, copy, CancellationToken.None));
        var retained = (await store.GetAsync(copy.RunId, CancellationToken.None))!;
        Assert.Single(retained.Conversation);
        Assert.Equal(2, retained.ModelTurns);
        Assert.Equal(1, retained.Version);
    }

    [Fact]
    public async Task Another_runner_cannot_resume_a_run_using_different_trusted_composition()
    {
        var f = new Fixture();
        var paused = await f.PauseAsync();
        var other = new AgentRunner(f.Model, f.Tools, f.Executor, new(), runStore: f.Store);
        Assert.Equal(ApprovalSubmissionStatus.Conflict, (await other.SubmitApprovalAsync(Command(paused))).Status);
        Assert.Empty(f.Executor.Calls);
        Assert.Equal(RunStatus.Completed, (await f.Runner.SubmitApprovalAsync(Command(paused))).RunResult!.Status);
    }

    private static ApprovalCommand Command(AgentRunResult paused, ApprovalChoice choice = ApprovalChoice.Approve) =>
        new(paused.RunId, paused.PendingApproval!.ApprovalId, choice);
    private static bool IsTerminal(ExecutionEventType type) => type is ExecutionEventType.RunCompleted
        or ExecutionEventType.RunFailed or ExecutionEventType.RunCancelled or ExecutionEventType.RunLimitReached;

    private sealed class Fixture
    {
        public Model Model { get; } = new();
        public Tools Tools { get; } = new();
        public Executor Executor { get; } = new();
        public Policy Policy { get; } = new();
        public Events Events { get; } = new();
        public IRunStateStore Store { get; }
        public AgentRunner Runner { get; }
        public Fixture(RunLimits? limits = null, IRunStateStore? store = null)
        {
            Store = store ?? new InMemoryRunStateStore();
            Runner = new(Model, Tools, Executor, limits ?? new(), Events, Policy, Store);
        }
        public async Task<AgentRunResult> PauseAsync()
        {
            var result = await Runner.RunAsync(new("input"));
            Assert.Equal(RunStatus.AwaitingApproval, result.Status);
            Assert.NotNull(result.PendingApproval);
            return result;
        }
    }
    private sealed class Model : IModelProvider
    {
        public List<ModelRequest> Requests { get; } = [];
        public Func<ModelRequest, ModelReply> Script { get; set; } = request => request.Messages.Count == 1 ? Proposal(Call()) : Final();
        public Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Requests.Add(request); return Task.FromResult(Script(request)); }
    }
    private sealed class Tools : IToolProvider
    {
        public IReadOnlyList<ToolDefinition> Catalog { get; set; } = [Tool];
        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken token) => Task.FromResult(Catalog);
    }
    private sealed class Executor : IToolExecutor
    {
        public List<ToolCall> Calls { get; } = [];
        public Action? BeforeExecute { get; set; }
        public Task<ToolResult> ExecuteAsync(ToolDefinition tool, ToolCall call, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); BeforeExecute?.Invoke(); Calls.Add(call);
            return Task.FromResult(new ToolResult(call.CallId, true, JsonSerializer.SerializeToElement(new { done = true })));
        }
    }
    private sealed class Policy : IPolicyEvaluator
    {
        public PolicyDecision Current { get; set; } = new(PolicyOutcome.RequireApproval, "policy-v1", "Confirm operation");
        public Func<PolicyEvaluationContext, PolicyDecision>? Script { get; set; }
        public int Evaluations { get; private set; }
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyEvaluationContext context, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Evaluations++; return ValueTask.FromResult(Script?.Invoke(context) ?? Current); }
    }
    private sealed class Events : IExecutionEventSink
    {
        public List<ExecutionEvent> Items { get; } = [];
        public bool Throw { get; set; }
        public Func<ExecutionEvent, Task>? Observe { get; set; }
        public async ValueTask PublishAsync(ExecutionEvent e, CancellationToken token)
        {
            Items.Add(e);
            if (Observe is not null) await Observe(e);
            if (Throw) throw new InvalidOperationException("Observer failed.");
        }
    }
    private sealed class StoreWrapper : IRunStateStore
    {
        private readonly InMemoryRunStateStore _inner = new();
        private readonly TaskCompletionSource _readsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        public bool SynchronizeTwoReads { get; init; }
        public bool FailCreate { get; init; }
        public Action? AfterClaim { get; set; }
        public ValueTask CreateAsync(RunState state, CancellationToken token) => FailCreate
            ? throw new InvalidOperationException("Store unavailable.") : _inner.CreateAsync(state, token);
        public async ValueTask<RunState?> GetAsync(Guid id, CancellationToken token)
        {
            var state = await _inner.GetAsync(id, token);
            if (SynchronizeTwoReads)
            {
                if (Interlocked.Increment(ref _reads) == 2) _readsReady.TrySetResult();
                await _readsReady.Task.WaitAsync(token);
            }
            return state;
        }
        public async ValueTask<bool> TryReplaceAsync(Guid id, long version, RunState state, CancellationToken token)
        {
            var won = await _inner.TryReplaceAsync(id, version, state, token);
            if (won && state.Lifecycle == RunLifecycleState.Running) AfterClaim?.Invoke();
            return won;
        }
    }
}
