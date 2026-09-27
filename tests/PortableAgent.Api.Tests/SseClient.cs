using System.Globalization;
using System.Net;
using System.Text.Json;
using PortableAgent.Api.Streaming;

namespace PortableAgent.Api.Tests;

internal sealed record SseFrame(long? Id, string? Event, SseEventDto? Data, string? Comment);

internal sealed class SseClient(HttpResponseMessage response, StreamReader reader) : IDisposable
{
    public List<SseFrame> Frames { get; } = [];
    public SseEventDto[] Events => Frames.Where(f => f.Data is not null).Select(f => f.Data!).ToArray();
    public static async Task<SseClient> OpenAsync(HttpClient client, Guid runId, string query = "", string? lastEventId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/runs/{runId}/events{query}");
        if (lastEventId is not null) request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        return new(response, new StreamReader(await response.Content.ReadAsStreamAsync()));
    }

    public async Task<SseFrame?> ReadAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        long? id = null;
        string? type = null, data = null, comment = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) return null;
            if (line.Length == 0)
            {
                if (data is null && comment is null) continue;
                var dto = data is null ? null : JsonSerializer.Deserialize<SseEventDto>(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var frame = new SseFrame(id, type, dto, comment);
                Frames.Add(frame);
                return frame;
            }
            if (line.StartsWith("id: ")) id = long.Parse(line[4..], CultureInfo.InvariantCulture);
            else if (line.StartsWith("event: ")) type = line[7..];
            else if (line.StartsWith("data: ")) data = line[6..];
            else if (line.StartsWith(": ")) comment = line[2..];
            else Assert.Fail($"Unexpected SSE line: {line}");
        }
    }

    public async Task UntilAsync(string eventType)
    {
        while (true)
        {
            var frame = await ReadAsync();
            Assert.NotNull(frame);
            if (frame.Event == eventType) return;
        }
    }

    public async Task ReadToEndAsync() { while (await ReadAsync() is not null) { } }
    public void Dispose() { reader.Dispose(); response.Dispose(); }
}
