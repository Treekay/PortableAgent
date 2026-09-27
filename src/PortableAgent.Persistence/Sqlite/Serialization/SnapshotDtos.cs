using System.Text.Json;

namespace PortableAgent.Persistence.Sqlite.Serialization;

// Storage contracts deliberately independent from Core constructors and enum numbering.
internal sealed record SnapshotEnvelope(int SchemaVersion, StateDto State);
internal sealed record StateDto(Guid RunId, string RuntimeDefinitionId, string Lifecycle, long Version,
    MessageDto[] Conversation, ToolDto[] ToolCatalog, ApprovalDto? PendingApproval,
    ApprovalDto[] ResolvedApprovals, int ModelTurns, int ToolCalls, LimitsDto Limits,
    string[] UsedCallIds, long LastSequence);
internal sealed record LimitsDto(int MaxModelTurns, int MaxToolCalls);
internal sealed record MessageDto(string Role, string? Text, CallDto[] ToolCalls, ResultDto? ToolResult);
internal sealed record ToolDto(string SourceId, string Name, string ModelName, string Description, JsonElement InputSchema);
internal sealed record CallDto(string CallId, string ToolName, JsonElement Arguments);
internal sealed record ResultDto(string CallId, bool IsSuccess, JsonElement? Output, string? Error, string Disposition);
internal sealed record ApprovalDto(Guid ApprovalId, Guid RunId, ToolDto ToolDefinition, CallDto ToolCall,
    string? PolicyId, string? PolicyReason, DateTimeOffset CreatedAt, string Status);
