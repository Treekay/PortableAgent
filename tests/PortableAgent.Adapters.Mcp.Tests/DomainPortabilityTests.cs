using System.Text.Json;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;
using PortableAgent.Core.Models;
using PortableAgent.Core.Policies;
using PortableAgent.Core.Tools;
using PortableAgent.Infrastructure.Models;
using PortableAgent.Infrastructure.Policies;
using PortableAgent.Persistence.Sqlite;

namespace PortableAgent.Adapters.Mcp.Tests;

public sealed class DomainPortabilityTests
{
    internal static AgentRunner Runner(McpToolAdapter adapter, string path, bool pet = false,
        PolicyDecision? cancellation = null, IModelProvider? model = null) => new(
        pet ? "pet-mcp-demo-v1" : "flight-mcp-demo-v1",
        model ?? (pet ? new PetBoardingScriptedModelProvider() : new FlightBookingScriptedModelProvider()),
        adapter, adapter, new(), policyEvaluator: new InMemoryPolicyEvaluator(pet
            ? new Dictionary<ToolId, PolicyDecision>
            {
                [new("pet-mcp", "get_care_records")] = new(PolicyOutcome.Allow),
                [new("pet-mcp", "create_staff_task")] = new(PolicyOutcome.Deny)
            }
            : new Dictionary<ToolId, PolicyDecision>
            {
                [new("flight-mcp", "get_booking")] = new(PolicyOutcome.Allow),
                [new("flight-mcp", "cancel_booking")] = cancellation
                    ?? new(PolicyOutcome.RequireApproval, "confirm-flight-cancellation-v1")
            }), runStore: new SqliteRunStateStore(path));

    [Theory]
    [InlineData(true, "Has Cooper eaten today?", "get_care_records", "Yes. Cooper was fed at 08:00.")]
    [InlineData(false, "Show my booking.", "get_booking", "Booking NZ123 is confirmed from Auckland to Sydney on 2026-10-10.")]
    public async Task Same_concrete_adapter_and_existing_models_complete_both_domains(bool pet, string prompt, string tool, string answer)
    {
        await using var server = await DomainServerFixture.StartAsync(pet);
        using var db = await TestDatabase.CreateAsync();
        var source = pet ? "pet-mcp" : "flight-mcp";
        await using var adapter = new McpToolAdapter(new(source, server.Endpoint));
        Assert.IsType<McpToolAdapter>(adapter);
        var result = await Runner(adapter, db.Path, pet).RunAsync(new(prompt));
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(answer, result.FinalText);
        Assert.Equal(1, server.Discoveries);
        Assert.Single(server.Calls(tool));
        var state = (await db.Store.GetAsync(result.RunId, default))!;
        Assert.Equal(pet ? "pet-mcp-demo-v1" : "flight-mcp-demo-v1", state.RuntimeDefinitionId);
        Assert.Contains(state.ToolCatalog, t => t.Id == new ToolId(source, tool) && t.ModelName == tool);
        Assert.All(state.ToolCatalog, t => Assert.Equal(source, t.Id.SourceId));
        Assert.Equal(ToolResultDisposition.Executed, state.Conversation[2].ToolResult!.Disposition);
    }

    [Fact]
    public async Task Pet_denial_completes_without_any_remote_write_or_execution_event()
    {
        await using var server = await DomainServerFixture.StartAsync(pet: true);
        using var db = await TestDatabase.CreateAsync();
        await using var adapter = new McpToolAdapter(new("pet-mcp", server.Endpoint));
        var result = await Runner(adapter, db.Path, pet: true).RunAsync(new("Ask the staff to give Cooper some fresh water."));
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal("The staff task was not created because the operation was not permitted.", result.FinalText);
        Assert.Equal(1, server.Discoveries);
        Assert.Empty(server.Calls("create_staff_task"));
        Assert.Empty(server.Pet.Tasks);
        var state = (await db.Store.GetAsync(result.RunId, default))!;
        Assert.Equal(ToolResultDisposition.DeniedByPolicy, state.Conversation[2].ToolResult!.Disposition);
        var events = await db.Store.ReadEventsAfterAsync(result.RunId, 0, 100, default);
        Assert.DoesNotContain(events, e => e.EventType is ExecutionEventType.ToolExecutionStarted or ExecutionEventType.ToolExecutionCompleted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_discovery_publishes_exact_static_schemas(bool pet)
    {
        await using var server = await DomainServerFixture.StartAsync(pet);
        await using var adapter = new McpToolAdapter(new(pet ? "pet-mcp" : "flight-mcp", server.Endpoint));
        var tools = await adapter.GetToolsAsync(default);
        Assert.Equal(2, tools.Count);
        foreach (var tool in tools)
        {
            var expected = tool.ModelName switch
            {
                "get_care_records" => """{"type":"object","properties":{"petName":{"type":"string"}},"required":["petName"],"additionalProperties":false}""",
                "create_staff_task" => """{"type":"object","properties":{"petName":{"type":"string"},"task":{"type":"string"}},"required":["petName","task"],"additionalProperties":false}""",
                _ => """{"type":"object","properties":{"bookingId":{"type":"string"}},"required":["bookingId"],"additionalProperties":false}"""
            };
            using var document = JsonDocument.Parse(expected);
            Assert.True(JsonElement.DeepEquals(document.RootElement, tool.InputSchema));
        }
    }

    [Fact]
    public async Task Pet_business_write_really_creates_tasks_with_independent_ids()
    {
        await using var server = await DomainServerFixture.StartAsync(pet: true);
        await using var adapter = new McpToolAdapter(new("pet-mcp", server.Endpoint));
        var tool = (await adapter.GetToolsAsync(default)).Single(t => t.ModelName == "create_staff_task");
        for (var i = 1; i <= 2; i++)
        {
            var result = await adapter.ExecuteAsync(tool, new($"call-{i}", tool.ModelName,
                JsonSerializer.SerializeToElement(new { petName = "Cooper", task = "Give fresh water" })), default);
            Assert.True(result.IsSuccess);
            Assert.Equal($"task-{i}", result.Output!.Value.GetProperty("taskId").GetString());
        }
        Assert.Equal(2, server.Calls("create_staff_task").Length);
        Assert.Equal(2, server.Pet.Tasks.Count);
    }

    [Fact]
    public async Task Repeated_remote_cancellation_has_two_calls_but_one_mutation_and_read_returns_real_state()
    {
        await using var server = await DomainServerFixture.StartAsync();
        await using var adapter = new McpToolAdapter(new("flight-mcp", server.Endpoint));
        var tools = await adapter.GetToolsAsync(default);
        var cancel = tools.Single(t => t.ModelName == "cancel_booking");
        var args = JsonSerializer.SerializeToElement(new { bookingId = "NZ123" });
        for (var i = 1; i <= 2; i++)
        {
            var result = await adapter.ExecuteAsync(cancel, new($"call-{i}", cancel.ModelName, args), default);
            Assert.True(result.IsSuccess);
            Assert.Equal("cancelled", result.Output!.Value.GetProperty("status").GetString());
        }
        Assert.Equal(2, server.Calls("cancel_booking").Length);
        Assert.Equal(2, server.Flight.CancelCallCount);
        Assert.Equal(1, server.Flight.CancelMutationCount);
        var read = tools.Single(t => t.ModelName == "get_booking");
        var current = await adapter.ExecuteAsync(read, new("read", read.ModelName, args), default);
        Assert.Equal("cancelled", current.Output!.Value.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(true, "create_staff_task", "{\"petName\":\"Unknown\",\"task\":\"water\"}")]
    [InlineData(true, "create_staff_task", "{\"petName\":\"Cooper\",\"task\":\" \"}")]
    [InlineData(true, "get_care_records", "{\"petName\":\"Unknown\"}")]
    [InlineData(false, "cancel_booking", "{\"bookingId\":\"Unknown\"}")]
    [InlineData(false, "get_booking", "{\"bookingId\":\"Unknown\"}")]
    [InlineData(false, "cancel_booking", "{\"bookingId\":\"NZ123\",\"approvalId\":\"invalid\"}")]
    public async Task Business_validation_returns_executed_error_without_mutation(bool pet, string name, string json)
    {
        await using var server = await DomainServerFixture.StartAsync(pet);
        await using var adapter = new McpToolAdapter(new(pet ? "pet-mcp" : "flight-mcp", server.Endpoint));
        var tool = (await adapter.GetToolsAsync(default)).Single(t => t.ModelName == name);
        using var args = JsonDocument.Parse(json);
        var result = await adapter.ExecuteAsync(tool, new("call", name, args.RootElement), default);
        Assert.False(result.IsSuccess);
        Assert.Equal(ToolResultDisposition.Executed, result.Disposition);
        Assert.NotEmpty(result.Error!);
        Assert.Single(server.Calls(name));
        Assert.Empty(server.Pet.Tasks);
        Assert.Equal(0, server.Flight.CancelMutationCount);
        Assert.Equal("confirmed", server.Flight.GetBooking("NZ123").Status);
    }
}

internal sealed class TestDatabase : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"portable-domain-{Guid.NewGuid():N}.db");
    public SqliteRunStateStore Store => new(Path);
    public static async Task<TestDatabase> CreateAsync()
    {
        var db = new TestDatabase();
        await SqliteRunStateStore.InitializeAsync(db.Path);
        return db;
    }
    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(Path + suffix);
    }
}
