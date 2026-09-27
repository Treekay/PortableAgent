using System.Text.Json;
using PortableAgent.Core.Tools;

namespace PortableAgent.Infrastructure.Tools.PetBoarding;

/// <summary>Stateless local simulation. No external services or persistent side effects.</summary>
public sealed class PetBoardingToolExecutor : IToolExecutor
{
    public Task<ToolResult> ExecuteAsync(
        ToolDefinition registeredTool, ToolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Dispatch only by the trusted definition resolved by AgentRunner.
        var isRead = registeredTool.Id == PetBoardingToolProvider.ReadId;
        if (!isRead && registeredTool.Id != PetBoardingToolProvider.ActionId)
            return Task.FromResult(new ToolResult(call.CallId, false, Error: "Unknown PetBoarding tool identity."));

        string[] fields = isRead ? ["petName"] : ["petName", "task"];
        var args = call.Arguments;
        if (args.ValueKind != JsonValueKind.Object
            || args.EnumerateObject().Count() != fields.Length
            || fields.Any(field => !args.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            || (fields.Contains("petName") && args.GetProperty("petName").GetString() != "Cooper")
            || (fields.Contains("task") && args.GetProperty("task").GetString() != "Give fresh water"))
            return Task.FromResult(new ToolResult(call.CallId, false, Error: "Unsupported demo arguments."));

        var json = isRead
            ? """{"petName":"Cooper","records":[{"type":"feeding","time":"08:00","status":"completed"}]}"""
            : """{"taskId":"task-1","petName":"Cooper","task":"Give fresh water","status":"created"}""";
        using var document = JsonDocument.Parse(json);
        return Task.FromResult(new ToolResult(call.CallId, true, document.RootElement.Clone()));
    }
}
