using System.Text.Json;
using PortableAgent.Core.Models;
using PortableAgent.Core.Tools;

namespace PortableAgent.Infrastructure.Models;

/// <summary>Deterministic tool-contract demo; deliberately unaware of its execution protocol.</summary>
public sealed class ServerStatusScriptedModelProvider : IModelProvider
{
    public Task<ModelReply> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string name = "get_server_status";
        const string id = "status-call-1";
        if (request.Messages.Count == 0 || request.Messages[0].Role != MessageRole.User
            || request.Messages[0].Text != "Check the sample service status."
            || !request.Tools.Any(t => t.ModelName == name))
            throw new InvalidOperationException("Unsupported service status demo input or missing tool.");
        var empty = JsonSerializer.SerializeToElement(new { });
        if (request.Messages.Count == 1)
            return Task.FromResult(new ModelReply(null, [new(id, name, empty)], ModelFinishReason.ToolCalls));
        if (request.Messages.Count != 3 || request.Messages[1].Role != MessageRole.Assistant
            || request.Messages[1].ToolCalls.Count != 1
            || request.Messages[1].ToolCalls[0] is not { CallId: id, ToolName: name } proposal
            || !JsonElement.DeepEquals(proposal.Arguments, empty)
            || request.Messages[2].Role != MessageRole.Tool
            || request.Messages[2].ToolResult is not { CallId: id, IsSuccess: true, Disposition: ToolResultDisposition.Executed, Output: { } output }
            || output.ValueKind != JsonValueKind.Object
            || !output.TryGetProperty("service", out var service) || service.ValueKind != JsonValueKind.String || service.GetString() != "sample"
            || !output.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "ok")
            throw new InvalidOperationException("Unexpected service status conversation or tool result.");
        return Task.FromResult(new ModelReply("The sample service is online.", [], ModelFinishReason.Completed));
    }
}
