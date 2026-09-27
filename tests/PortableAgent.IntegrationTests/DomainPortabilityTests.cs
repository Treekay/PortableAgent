using System.Text.Json;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;
using PortableAgent.Infrastructure.Models;
using PortableAgent.Infrastructure.Tools.PetBoarding;
using PortableAgent.Infrastructure.Tools.FlightBooking;

namespace PortableAgent.IntegrationTests;

public sealed class DomainPortabilityTests
{
    [Theory]
    [InlineData(true, false, "Has Cooper eaten today?", "get_care_records", "care.get_records",
        "Yes. Cooper was fed at 08:00.")]
    [InlineData(true, true, "Ask the staff to give Cooper some fresh water.", "create_staff_task", "staff.create_task",
        "Task task-1 was created: Give fresh water to Cooper.")]
    [InlineData(false, false, "Show my booking.", "get_booking", "booking.get",
        "Booking NZ123 is confirmed from Auckland to Sydney on 2026-10-10.")]
    [InlineData(false, true, "Cancel my booking.", "cancel_booking", "booking.cancel",
        "Booking NZ123 has been cancelled.")]
    public async Task Same_runner_completes_each_real_domain_composition(
        bool pet, bool action, string input, string toolName, string internalName, string expectedText)
    {
        var model = new RecordingModel(pet
            ? new PetBoardingScriptedModelProvider() : new FlightBookingScriptedModelProvider());
        var tools = new RecordingToolProvider(pet
            ? new PetBoardingToolProvider() : new FlightBookingToolProvider());
        var executor = new RecordingExecutor(pet
            ? new PetBoardingToolExecutor() : new FlightBookingToolExecutor());

        // Only the three Infrastructure dependencies change; the Runner is always the same Core type.
        var runner = new AgentRunner(model, tools, executor, new RunLimits());
        var result = await runner.RunAsync(new AgentRunRequest(input));

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(expectedText, result.FinalText);
        Assert.Equal(1, tools.Calls);
        string[] expectedCatalog = pet ? ["get_care_records", "create_staff_task"] : ["get_booking", "cancel_booking"];
        Assert.Equal(expectedCatalog, tools.Catalog.Select(tool => tool.ModelName));
        var source = pet ? "pet-local" : "flight-local";
        Assert.All(tools.Catalog, tool => Assert.Equal(source, tool.Id.SourceId));
        Assert.Equal(2, model.Requests.Count);
        Assert.Single(model.Requests[0].Messages);
        Assert.All(model.Requests, request => Assert.Equal(expectedCatalog, request.Tools.Select(tool => tool.ModelName)));
        Assert.Equal(1, executor.Calls);
        var executed = Assert.Single(executor.Executions);
        Assert.Equal(new ToolId(source, internalName), executed.Definition.Id);
        Assert.Equal(toolName, executed.Definition.ModelName);
        Assert.Equal(toolName, executed.Call.ToolName);
        var expectedArguments = pet
            ? action ? JsonSerializer.SerializeToElement(new { petName = "Cooper", task = "Give fresh water" })
                     : JsonSerializer.SerializeToElement(new { petName = "Cooper" })
            : JsonSerializer.SerializeToElement(new { bookingId = "NZ123" });
        Assert.True(JsonElement.DeepEquals(expectedArguments, executed.Call.Arguments));
        Assert.False(string.IsNullOrWhiteSpace(executed.Call.CallId));
        Assert.Equal(executed.Call.CallId, executed.Result.CallId);
        Assert.True(executed.Result.IsSuccess);
        var messages = model.Requests[1].Messages;
        Assert.Equal(3, messages.Count);
        Assert.Equal(MessageRole.User, messages[0].Role);
        Assert.Equal(input, messages[0].Text);
        Assert.Equal(MessageRole.Assistant, messages[1].Role);
        Assert.Equal(executed.Call.CallId, Assert.Single(messages[1].ToolCalls).CallId);
        Assert.Equal(MessageRole.Tool, messages[2].Role);
        var returned = Assert.IsType<ToolResult>(messages[2].ToolResult);
        Assert.Equal(executed.Call.CallId, returned.CallId);
        Assert.True(JsonElement.DeepEquals(executed.Result.Output!.Value, returned.Output!.Value));
        if (pet && !action)
            Assert.Equal("08:00", returned.Output.Value.GetProperty("records")[0].GetProperty("time").GetString());
        else
            Assert.Equal(pet ? "created" : action ? "cancelled" : "confirmed",
                returned.Output.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Pet_catalog_rejects_flight_tool_before_executor_is_invoked()
    {
        var tools = new RecordingToolProvider(new PetBoardingToolProvider());
        var executor = new RecordingExecutor(new PetBoardingToolExecutor());
        var runner = new AgentRunner(new CrossDomainModel(), tools, executor, new RunLimits());

        var result = await runner.RunAsync(new AgentRunRequest("Attempt cross-domain call."));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal("Unknown tool: get_booking.", result.Error);
        Assert.Equal(1, tools.Calls);
        Assert.Equal(0, executor.Calls);
        Assert.Empty(executor.Executions);
    }

    [Theory]
    [InlineData(true, "Has Cooper eaten today?")]
    [InlineData(true, "Ask the staff to give Cooper some fresh water.")]
    [InlineData(false, "Show my booking.")]
    [InlineData(false, "Cancel my booking.")]
    public async Task Scripts_do_not_confirm_a_correlated_but_incorrect_result(bool pet, string input)
    {
        IModelProvider model = pet ? new PetBoardingScriptedModelProvider() : new FlightBookingScriptedModelProvider();
        IToolProvider tools = pet ? new PetBoardingToolProvider() : new FlightBookingToolProvider();
        var catalog = await tools.GetToolsAsync(CancellationToken.None);
        var user = AgentMessage.User(input);
        var proposal = await model.GenerateAsync(new ModelRequest([user], catalog), CancellationToken.None);
        var call = Assert.Single(proposal.ToolCalls);
        var wrong = JsonSerializer.SerializeToElement(new
        {
            petName = "Another pet", bookingId = "WRONG", status = "failed",
            taskId = "wrong-task", task = "Wrong task",
            records = new[] { new { type = "feeding", time = "09:00", status = "pending" } }
        });
        var request = new ModelRequest([user, AgentMessage.AssistantToolRequest(proposal.Text, proposal.ToolCalls),
            AgentMessage.FromToolResult(new ToolResult(call.CallId, true, wrong))], catalog);

        await Assert.ThrowsAsync<InvalidOperationException>(() => model.GenerateAsync(request, CancellationToken.None));
    }

    private sealed class CrossDomainModel : IModelProvider
    {
        public Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelReply(null,
                [new ToolCall("cross-1", "get_booking", JsonSerializer.SerializeToElement(new { bookingId = "NZ123" }))],
                ModelFinishReason.ToolCalls));
    }
}
