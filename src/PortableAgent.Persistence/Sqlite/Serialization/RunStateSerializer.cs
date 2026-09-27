using System.Text.Json;
using System.Text.Json.Serialization;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;

namespace PortableAgent.Persistence.Sqlite.Serialization;

public static class RunStateSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string Serialize(RunState state) => JsonSerializer.Serialize(new SnapshotEnvelope(1,
        new(state.RunId, state.RuntimeDefinitionId, state.Lifecycle.ToString(), state.Version,
            state.Conversation.Select(m => new MessageDto(m.Role.ToString(), m.Text,
                m.ToolCalls.Select(ToDto).ToArray(), m.ToolResult is { } r ? ToDto(r) : null)).ToArray(),
            state.ToolCatalog.Select(ToDto).ToArray(), state.PendingApproval is { } p ? ToDto(p) : null,
            state.ResolvedApprovals.Select(ToDto).ToArray(), state.ModelTurns, state.ToolCalls,
            new(state.Limits.MaxModelTurns, state.Limits.MaxToolCalls),
            state.UsedCallIds.Order(StringComparer.Ordinal).ToArray(), state.LastSequence)), Options);

    public static RunState Deserialize(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<SnapshotEnvelope>(json, Options)
                ?? throw new InvalidDataException("Snapshot is null.");
            if (envelope.SchemaVersion != 1)
                throw new InvalidDataException($"Unsupported snapshot schemaVersion: {envelope.SchemaVersion}.");
            var s = envelope.State;
            var state = new RunState
            {
                RunId = s.RunId, RuntimeDefinitionId = s.RuntimeDefinitionId,
                Lifecycle = ParseEnum<RunLifecycleState>(s.Lifecycle), Version = s.Version,
                Conversation = s.Conversation.Select(FromDto).ToList(),
                ToolCatalog = s.ToolCatalog.Select(FromDto).ToList(),
                PendingApproval = s.PendingApproval is { } p ? FromDto(p) : null,
                ResolvedApprovals = s.ResolvedApprovals.Select(FromDto).ToList(),
                ModelTurns = s.ModelTurns, ToolCalls = s.ToolCalls,
                Limits = new(s.Limits.MaxModelTurns, s.Limits.MaxToolCalls),
                UsedCallIds = new(s.UsedCallIds, StringComparer.Ordinal), LastSequence = s.LastSequence
            };
            if (state.RunId == Guid.Empty || string.IsNullOrWhiteSpace(state.RuntimeDefinitionId)
                || state.Version < 0 || state.LastSequence < 0 || state.ModelTurns < 0 || state.ToolCalls < 0
                || state.Limits.MaxModelTurns < 1 || state.Limits.MaxToolCalls < 0
                || state.ModelTurns > state.Limits.MaxModelTurns || state.ToolCalls > state.Limits.MaxToolCalls
                || s.UsedCallIds.Any(string.IsNullOrWhiteSpace) || state.UsedCallIds.Count != s.UsedCallIds.Length)
                throw new InvalidDataException("Invalid Run identity, counters or limits.");
            if ((state.Lifecycle == RunLifecycleState.AwaitingApproval) != (state.PendingApproval is not null))
                throw new InvalidDataException("Pending approval does not match lifecycle.");
            var approvals = state.ResolvedApprovals.ToList();
            if (state.PendingApproval is { } pending)
            {
                if (pending.Status != ApprovalStatus.Pending || !state.UsedCallIds.Contains(pending.ToolCall.CallId)
                    || state.Conversation.LastOrDefault() is not { Role: MessageRole.Assistant } last
                    || last.ToolCalls.Count != 1 || last.ToolCalls[0].CallId != pending.ToolCall.CallId
                    || last.ToolCalls[0].ToolName != pending.ToolCall.ToolName
                    || !JsonElement.DeepEquals(last.ToolCalls[0].Arguments, pending.ToolCall.Arguments))
                    throw new InvalidDataException("Pending approval is not the frozen assistant call.");
                approvals.Add(pending);
            }
            if (state.ResolvedApprovals.Any(a => a.Status == ApprovalStatus.Pending)
                || approvals.Any(a => a.RunId != state.RunId || a.ApprovalId == Guid.Empty
                    || a.ToolCall.ToolName != a.ToolDefinition.ModelName)
                || approvals.Select(a => a.ApprovalId).Distinct().Count() != approvals.Count)
                throw new InvalidDataException("Invalid approval history.");
            return state;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            throw new InvalidDataException("Malformed RunState snapshot.", ex);
        }
    }

    internal static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, false, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == value
            ? parsed : throw new InvalidDataException($"Unknown {typeof(T).Name}: {value}.");

    private static ToolDto ToDto(ToolDefinition t) => new(t.Id.SourceId, t.Id.Name, t.ModelName, t.Description, t.InputSchema);
    private static CallDto ToDto(ToolCall c) => new(c.CallId, c.ToolName, c.Arguments);
    private static ResultDto ToDto(ToolResult r) => new(r.CallId, r.IsSuccess, r.Output, r.Error, r.Disposition.ToString());
    private static ApprovalDto ToDto(PendingApproval a) => new(a.ApprovalId, a.RunId, ToDto(a.ToolDefinition),
        ToDto(a.ToolCall), a.PolicyId, a.PolicyReason, a.CreatedAt, a.Status.ToString());
    private static ToolDefinition FromDto(ToolDto t) => new(new(t.SourceId, t.Name), t.ModelName, t.Description, Own(t.InputSchema));
    private static ToolCall FromDto(CallDto c) => new(c.CallId, c.ToolName, Own(c.Arguments));
    private static ToolResult FromDto(ResultDto r) => new(r.CallId, r.IsSuccess, r.Output is { } o ? Own(o) : null,
        r.Error, ParseEnum<ToolResultDisposition>(r.Disposition));
    private static PendingApproval FromDto(ApprovalDto a) => new(a.ApprovalId, a.RunId, FromDto(a.ToolDefinition),
        FromDto(a.ToolCall), a.PolicyId, a.PolicyReason, a.CreatedAt, ParseEnum<ApprovalStatus>(a.Status));
    private static JsonElement Own(JsonElement value) => value.ValueKind != JsonValueKind.Undefined
        ? value.Clone() : throw new InvalidDataException("Missing JSON value.");

    private static AgentMessage FromDto(MessageDto m)
    {
        var role = ParseEnum<MessageRole>(m.Role);
        return role switch
        {
            MessageRole.User when m.ToolCalls.Length == 0 && m.ToolResult is null => AgentMessage.User(m.Text!),
            MessageRole.Assistant when m.ToolResult is null && m.ToolCalls.Length > 0 =>
                AgentMessage.AssistantToolRequest(m.Text, m.ToolCalls.Select(FromDto).ToArray()),
            MessageRole.Assistant when m.ToolResult is null => AgentMessage.AssistantFinal(m.Text!),
            MessageRole.Tool when m.Text is null && m.ToolCalls.Length == 0 && m.ToolResult is not null =>
                AgentMessage.FromToolResult(FromDto(m.ToolResult)),
            _ => throw new InvalidDataException("Invalid message shape.")
        };
    }
}
