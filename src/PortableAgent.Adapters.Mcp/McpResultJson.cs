using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace PortableAgent.Adapters.Mcp;

internal static class McpResultJson
{
    // SDK 2.2.0's convenience CallToolAsync conflates absent structuredContent and JSON null.
    // Use the SDK's typed request API with its own defaults plus this value converter.
    // The SDK still owns all protocol framing, request IDs, errors and transport processing.
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.Converters.Insert(0, new PresentJsonValueConverter());
        options.MakeReadOnly();
        return options;
    }

    private sealed class PresentJsonValueConverter : JsonConverter<JsonElement?>
    {
        public override bool HandleNull => true;
        public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonElement.ParseValue(ref reader);
        public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
        {
            if (value is { } element) element.WriteTo(writer);
            else writer.WriteNullValue();
        }
    }
}
