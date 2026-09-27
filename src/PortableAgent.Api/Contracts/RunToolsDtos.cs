using System.Text.Json;
using PortableAgent.Core.Execution;

namespace PortableAgent.Api.Contracts;

public sealed record ToolCatalogEntryDto(ToolIdentityDto ToolId, string ModelName,
    string Description, JsonElement InputSchema);

public sealed record RunToolsDto(Guid RunId, string Status, long SnapshotSequence,
    IReadOnlyList<ToolCatalogEntryDto> Tools)
{
    public static RunToolsDto FromState(RunState state) => new(state.RunId, state.Lifecycle.ToString(),
        state.LastSequence, state.ToolCatalog.Select(tool => new ToolCatalogEntryDto(
            new(tool.Id.SourceId, tool.Id.Name), tool.ModelName, tool.Description, tool.InputSchema.Clone())).ToArray());
}
