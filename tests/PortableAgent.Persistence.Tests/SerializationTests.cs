using System.Text.Json;
using System.Text.Json.Nodes;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;
using PortableAgent.Persistence.Sqlite.Serialization;

namespace PortableAgent.Persistence.Tests;

public sealed class SerializationTests
{
    private static RunState Sample()
    {
        using var doc = JsonDocument.Parse("{\"bookingId\":\"NZ123\"}");
        var tool = new ToolDefinition(new("flight-local", "booking.cancel"), "cancel_booking", "Cancel booking", doc.RootElement.Clone());
        var call = new ToolCall("Call", tool.ModelName, doc.RootElement.Clone());
        var old = call with { CallId = "call" };
        var id = Guid.NewGuid();
        var approval = new PendingApproval(Guid.NewGuid(), id, tool, call, "policy-v1", "Confirm",
            DateTimeOffset.UtcNow, ApprovalStatus.Pending);
        return new()
        {
            RunId = id, RuntimeDefinitionId = "flight-demo-v1", Lifecycle = RunLifecycleState.AwaitingApproval,
            Version = 3, Limits = new(6, 6), ModelTurns = 2, ToolCalls = 1, LastSequence = 19,
            ToolCatalog = [tool], UsedCallIds = new(["call", "Call"], StringComparer.Ordinal),
            PendingApproval = approval,
            ResolvedApprovals = [approval with { ApprovalId = Guid.NewGuid(), ToolCall = old, Status = ApprovalStatus.Approved }],
            Conversation = [AgentMessage.User("cancel"), AgentMessage.AssistantToolRequest(null, [old]),
                AgentMessage.FromToolResult(new(old.CallId, true, doc.RootElement)), AgentMessage.AssistantFinal("Done"),
                AgentMessage.User("again"), AgentMessage.AssistantToolRequest("confirm", [call])]
        };
    }

    [Fact]
    public void Complete_snapshot_round_trip_owns_json_and_collections()
    {
        var state = Sample();
        var json = RunStateSerializer.Serialize(state);
        var restored = RunStateSerializer.Deserialize(json);
        Assert.Equal(json, RunStateSerializer.Serialize(restored));
        Assert.Equal(StringComparer.Ordinal, restored.UsedCallIds.Comparer);
        Assert.Equal(2, restored.UsedCallIds.Count);
        Assert.DoesNotContain("CALL", restored.UsedCallIds);
        state.Conversation.Clear(); state.ToolCatalog.Clear(); state.UsedCallIds.Clear();
        Assert.Equal(6, restored.Conversation.Count);
        Assert.Equal("NZ123", restored.PendingApproval!.ToolCall.Arguments.GetProperty("bookingId").GetString());
        Assert.Equal("NZ123", restored.Conversation[2].ToolResult!.Output!.Value.GetProperty("bookingId").GetString());
        Assert.Equal("NZ123", restored.ToolCatalog[0].InputSchema.GetProperty("bookingId").GetString());
        Assert.Equal("NZ123", restored.ResolvedApprovals[0].ToolCall.Arguments.GetProperty("bookingId").GetString());
    }

    [Fact]
    public void Completed_batch_with_business_failure_and_skipped_result_round_trips_without_schema_change()
    {
        var state = Sample();
        var call = state.PendingApproval!.ToolCall;
        var calls = new[] { call with { CallId = "A" }, call with { CallId = "B" }, call with { CallId = "C" } };
        state.Lifecycle = RunLifecycleState.Completed;
        state.PendingApproval = null;
        state.ResolvedApprovals.Clear();
        state.UsedCallIds = new(["A", "B", "C"], StringComparer.Ordinal);
        state.ToolCalls = 2;
        state.Conversation = [AgentMessage.User("batch"), AgentMessage.AssistantToolRequest(null, calls),
            AgentMessage.FromToolResult(new("A", true, JsonSerializer.SerializeToElement(new { status = "ok" }))),
            AgentMessage.FromToolResult(new("B", false, JsonSerializer.SerializeToElement(new { code = "invalid" }), "Invalid input.")),
            AgentMessage.FromToolResult(new("C", false, Error: "Earlier call failed.", Disposition: ToolResultDisposition.NotExecutedDueToPriorFailure)),
            AgentMessage.AssistantFinal("The failure was handled.")];
        var json = RunStateSerializer.Serialize(state);
        var restored = RunStateSerializer.Deserialize(json);
        Assert.Equal(json, RunStateSerializer.Serialize(restored));
        Assert.Equal(1, JsonNode.Parse(json)!["schemaVersion"]!.GetValue<int>());
        var results = restored.Conversation.Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolResult!).ToArray();
        Assert.Equal(new[] { "A", "B", "C" }, results.Select(r => r.CallId));
        Assert.False(results[1].IsSuccess);
        Assert.Equal("Invalid input.", results[1].Error);
        Assert.Equal("invalid", results[1].Output!.Value.GetProperty("code").GetString());
        Assert.Equal(ToolResultDisposition.NotExecutedDueToPriorFailure, results[2].Disposition);
        Assert.Null(results[2].Output);
        Assert.Equal(2, restored.ToolCalls);
        Assert.Contains("C", restored.UsedCallIds);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("lifecycle")]
    [InlineData("role")]
    [InlineData("approval")]
    [InlineData("disposition")]
    [InlineData("numeric-enum")]
    [InlineData("message-shape")]
    [InlineData("missing-field")]
    [InlineData("null-element")]
    [InlineData("missing-pending")]
    [InlineData("frozen-arguments")]
    public void Invalid_snapshot_fails_clearly(string corruption)
    {
        var root = JsonNode.Parse(RunStateSerializer.Serialize(Sample()))!;
        var state = root["state"]!;
        switch (corruption)
        {
            case "schema": root["schemaVersion"] = 2; break;
            case "lifecycle": state["lifecycle"] = "Unknown"; break;
            case "role": state["conversation"]![0]!["role"] = "Unknown"; break;
            case "approval": state["resolvedApprovals"]![0]!["status"] = "Unknown"; break;
            case "disposition": state["conversation"]![2]!["toolResult"]!["disposition"] = "Unknown"; break;
            case "numeric-enum": state["lifecycle"] = "1"; break;
            case "message-shape": state["conversation"]![0]!["toolResult"] = state["conversation"]![2]!["toolResult"]!.DeepClone(); break;
            case "missing-field": state.AsObject().Remove("limits"); break;
            case "null-element": state["conversation"]![0] = null; break;
            case "missing-pending": state["pendingApproval"] = null; break;
            case "frozen-arguments": state["pendingApproval"]!["toolCall"]!["arguments"]!["bookingId"] = "forged"; break;
        }
        var error = Assert.Throws<InvalidDataException>(() => RunStateSerializer.Deserialize(root.ToJsonString()));
        if (corruption == "schema") Assert.Contains("schemaVersion: 2", error.Message);
    }
}
