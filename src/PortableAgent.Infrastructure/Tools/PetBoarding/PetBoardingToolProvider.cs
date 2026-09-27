using System.Text.Json;
using PortableAgent.Core.Tools;

namespace PortableAgent.Infrastructure.Tools.PetBoarding;

public sealed class PetBoardingToolProvider : IToolProvider
{
    internal static readonly ToolId ReadId = new("pet-local", "care.get_records");
    internal static readonly ToolId ActionId = new("pet-local", "staff.create_task");

    public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var readSchema = JsonDocument.Parse("""{"type":"object","properties":{"petName":{"type":"string"}},"required":["petName"],"additionalProperties":false}""");
        using var actionSchema = JsonDocument.Parse("""{"type":"object","properties":{"petName":{"type":"string"},"task":{"type":"string"}},"required":["petName","task"],"additionalProperties":false}""");
        // Returned elements outlive these temporary documents.
        IReadOnlyList<ToolDefinition> tools =
        [
            new(ReadId, "get_care_records", "Returns fixed PetBoarding demo data.", readSchema.RootElement.Clone()),
            new(ActionId, "create_staff_task", "Simulates staff.create_task; no state is persisted.", actionSchema.RootElement.Clone())
        ];
        return Task.FromResult(tools);
    }
}
