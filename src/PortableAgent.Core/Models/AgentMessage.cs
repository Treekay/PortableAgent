using PortableAgent.Core.Tools;

namespace PortableAgent.Core.Models;

public enum MessageRole { User, Assistant, Tool }

public sealed class AgentMessage
{
    public MessageRole Role { get; }
    public string? Text { get; }
    public IReadOnlyList<ToolCall> ToolCalls { get; }
    public ToolResult? ToolResult { get; }

    private AgentMessage(MessageRole role, string? text,
        IReadOnlyList<ToolCall> toolCalls, ToolResult? toolResult)
    {
        Role = role;
        Text = text;
        ToolCalls = toolCalls;
        ToolResult = toolResult;
    }

    public static AgentMessage User(string text) => TextMessage(MessageRole.User, text);
    public static AgentMessage AssistantFinal(string text) => TextMessage(MessageRole.Assistant, text);

    private static AgentMessage TextMessage(MessageRole role, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new(role, text, Array.Empty<ToolCall>(), null);
    }

    public static AgentMessage AssistantToolRequest(string? text, IReadOnlyList<ToolCall> calls)
    {
        if (calls.Count == 0)
            throw new ArgumentException("A tool request must contain a call.", nameof(calls));

        // Own the JSON data and collection retained in conversation history.
        var snapshot = calls.Select(call => call with { Arguments = call.Arguments.Clone() }).ToArray();
        return new(MessageRole.Assistant, text, Array.AsReadOnly(snapshot), null);
    }

    public static AgentMessage FromToolResult(ToolResult result) =>
        new(MessageRole.Tool, null, Array.Empty<ToolCall>(),
            result with { Output = result.Output?.Clone() });
}
