using System.Text.Json;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;
using PortableAgent.Core.Tests.TestDoubles;

namespace PortableAgent.Core.Tests;

public sealed class AgentRunnerTests
{
    private static ToolCall Call(string id = "call-1", string name = "calculator_add") =>
        new(id, name, JsonSerializer.SerializeToElement(new { a = 2, b = 3 }));
    private static ModelReply Propose(params ToolCall[] calls) =>
        new("Progress, not the final answer.", calls, ModelFinishReason.ToolCalls);
    private static readonly AgentRunRequest Request = new("Calculate 2 + 3.");

    [Fact]
    public async Task Trusted_host_id_is_preserved_and_legacy_overload_allocates_distinct_ids()
    {
        var runner = new AgentRunner("test-runtime-v1", new RecordingModel(_ => new("Done.", [], ModelFinishReason.Completed)),
            new TestToolProvider(), new RecordingExecutor(), new());
        var id = Guid.NewGuid();
        Assert.Equal(id, (await runner.RunAsync(id, Request)).RunId);
        var first = (await runner.RunAsync(Request)).RunId;
        var second = (await runner.RunAsync(Request)).RunId;
        Assert.NotEqual(Guid.Empty, first);
        Assert.NotEqual(Guid.Empty, second);
        Assert.NotEqual(first, second);
        Assert.NotEqual(id, first);
    }

    [Fact]
    public async Task Empty_host_id_is_rejected_before_dependencies_are_called()
    {
        var model = new RecordingModel(_ => Propose(Call()));
        var tools = new TestToolProvider();
        var executor = new RecordingExecutor();
        var runner = new AgentRunner("test-runtime-v1", model, tools, executor, new());
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(Guid.Empty, Request));
        Assert.Empty(model.Requests);
        Assert.Equal(0, tools.Calls);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task Successful_run_passes_correlated_result_to_second_model_turn()
    {
        var model = new RecordingModel(request => request.Messages.Count == 1
            ? Propose(Call()) : new("The result is 5.", [], ModelFinishReason.Completed));
        var tools = new TestToolProvider();
        var executor = new RecordingExecutor();
        var result = await new AgentRunner("test-runtime-v1", model, tools, executor, new()).RunAsync(Request);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal("The result is 5.", result.FinalText);
        Assert.Equal(1, tools.Calls);
        Assert.Equal(2, model.Requests.Count);
        var executed = Assert.Single(executor.Calls);
        Assert.Equal(TestToolProvider.Add.Id, executed.Definition.Id);
        Assert.Equal(2, executed.Call.Arguments.GetProperty("a").GetInt32());
        Assert.Equal(3, executed.Call.Arguments.GetProperty("b").GetInt32());
        Assert.Single(model.Requests[0].Messages); // The first snapshot did not grow.
        Assert.Equal("calculator_add", Assert.Single(model.Requests[0].Tools).ModelName);
        Assert.Collection(model.Requests[1].Messages,
            message => { Assert.Equal(MessageRole.User, message.Role); Assert.Equal(Request.UserMessage, message.Text); },
            message => { Assert.Equal(MessageRole.Assistant, message.Role); Assert.Equal("call-1", Assert.Single(message.ToolCalls).CallId); },
            message =>
            {
                Assert.Equal(MessageRole.Tool, message.Role);
                Assert.NotNull(message.ToolResult);
                Assert.Equal("call-1", message.ToolResult.CallId);
                Assert.True(message.ToolResult.IsSuccess);
                Assert.Equal(5, message.ToolResult.Output!.Value.GetProperty("result").GetInt32());
            });
    }

    [Fact]
    public async Task Unknown_tool_fails_without_execution()
    {
        var model = new RecordingModel(_ => Propose(Call(name: "invented_tool")));
        var executor = new RecordingExecutor();
        var result = await new AgentRunner("test-runtime-v1", model, new TestToolProvider(), executor, new()).RunAsync(Request);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("Unknown tool", result.Error!);
        Assert.Empty(executor.Calls);
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(2, 10, 2, 2)]
    [InlineData(10, 1, 2, 1)]
    public async Task Repeated_calls_stop_before_exceeding_budgets(
        int maxTurns, int maxTools, int expectedTurns, int expectedTools)
    {
        var model = new RecordingModel(request => Propose(Call($"call-{request.Messages.Count}")));
        var executor = new RecordingExecutor();
        var result = await new AgentRunner("test-runtime-v1", model, new TestToolProvider(), executor,
            new(maxTurns, maxTools)).RunAsync(Request);
        Assert.Equal(RunStatus.LimitReached, result.Status);
        Assert.Equal(expectedTurns, model.Requests.Count);
        Assert.Equal(expectedTools, executor.Calls.Count);
    }

    [Fact]
    public async Task Tool_failure_reaches_model_as_correlated_result_without_automatic_retry()
    {
        var model = new RecordingModel(request => request.Messages.Count == 1 ? Propose(Call())
            : new("The calculation could not be completed.", [], ModelFinishReason.Completed));
        var output = JsonSerializer.SerializeToElement(new { code = "invalid_input" });
        var executor = new RecordingExecutor { Execute = call => new(call.CallId, false, output, "Tool failed.") };
        var result = await new AgentRunner("test-runtime-v1", model, new TestToolProvider(), executor, new()).RunAsync(Request);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal("The calculation could not be completed.", result.FinalText);
        Assert.Equal(2, model.Requests.Count);
        var message = model.Requests[1].Messages[^1];
        Assert.Equal(MessageRole.Tool, message.Role);
        Assert.Equal("call-1", message.ToolResult!.CallId);
        Assert.Equal(ToolResultDisposition.Executed, message.ToolResult.Disposition);
        Assert.False(message.ToolResult.IsSuccess);
        Assert.Equal("Tool failed.", message.ToolResult.Error);
        Assert.True(JsonElement.DeepEquals(output, message.ToolResult.Output!.Value));
        Assert.Single(executor.Calls);
    }

    [Fact]
    public async Task Precancelled_run_does_not_call_dependencies()
    {
        var model = new RecordingModel(_ => Propose(Call()));
        var tools = new TestToolProvider();
        var executor = new RecordingExecutor();
        var result = await new AgentRunner("test-runtime-v1", model, tools, executor, new())
            .RunAsync(Request, new CancellationToken(true));
        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Equal(0, tools.Calls);
        Assert.Empty(model.Requests);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task Mismatched_result_id_is_not_sent_to_model()
    {
        var model = new RecordingModel(_ => Propose(Call()));
        var executor = new RecordingExecutor { Execute = _ => new("wrong-id", true) };
        var result = await new AgentRunner("test-runtime-v1", model, new TestToolProvider(), executor, new()).RunAsync(Request);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("CallId", result.Error!);
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Duplicate_registry_identity_fails_before_model_call(bool duplicateModelName)
    {
        var original = TestToolProvider.Add;
        var duplicate = duplicateModelName
            ? original with { Id = new("another-source", "add") }
            : original with { ModelName = "other_add" };
        var tools = new TestToolProvider { Definitions = [original, duplicate] };
        var model = new RecordingModel(_ => Propose(Call()));
        var executor = new RecordingExecutor();
        var result = await new AgentRunner("test-runtime-v1", model, tools, executor, new()).RunAsync(Request);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Empty(model.Requests);
        Assert.Empty(executor.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("call-1")]
    public async Task Empty_or_reused_call_id_stops_execution(string secondId)
    {
        var model = new RecordingModel(request => Propose(Call(request.Messages.Count == 1 ? "call-1" : secondId)));
        var executor = new RecordingExecutor();
        var result = await new AgentRunner("test-runtime-v1", model, new TestToolProvider(), executor, new()).RunAsync(Request);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Single(executor.Calls);
    }

    [Fact]
    public async Task Whole_batch_is_checked_against_remaining_tool_budget()
    {
        var model = new RecordingModel(_ => Propose(Call("one"), Call("two")));
        var executor = new RecordingExecutor();
        var result = await new AgentRunner("test-runtime-v1", model, new TestToolProvider(), executor, new(4, 1)).RunAsync(Request);
        Assert.Equal(RunStatus.LimitReached, result.Status);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task Multiple_calls_preserve_complete_proposal_and_result_order()
    {
        var model = new RecordingModel(request => request.Messages.Count == 1
            ? Propose(Call("one"), Call("two")) : new("Done.", [], ModelFinishReason.Completed));
        var executor = new RecordingExecutor();
        var result = await new AgentRunner("test-runtime-v1", model, new TestToolProvider(), executor, new()).RunAsync(Request);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(new[] { "one", "two" }, executor.Calls.Select(item => item.Call.CallId));
        var messages = model.Requests[1].Messages;
        Assert.Equal(4, messages.Count);
        Assert.Equal(2, messages[1].ToolCalls.Count);
        Assert.Equal("one", messages[2].ToolResult!.CallId);
        Assert.Equal("two", messages[3].ToolResult!.CallId);
    }

    [Fact]
    public void Message_factories_own_json_beyond_temporary_document_lifetime()
    {
        AgentMessage proposal;
        AgentMessage result;
        using (var document = JsonDocument.Parse("{\"value\":5}"))
        {
            proposal = AgentMessage.AssistantToolRequest(null, [new("one", "tool", document.RootElement)]);
            result = AgentMessage.FromToolResult(new("one", true, document.RootElement));
        }
        Assert.Equal(5, proposal.ToolCalls[0].Arguments.GetProperty("value").GetInt32());
        Assert.Equal(5, result.ToolResult!.Output!.Value.GetProperty("value").GetInt32());
    }
}
