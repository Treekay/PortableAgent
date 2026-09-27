using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using PortableAgent.Core.Execution;
using PortableAgent.Core.Execution.Events;

namespace PortableAgent.Api.Streaming;

// Constructor injection allows short deterministic polling intervals in integration tests.
public sealed record RunEventStreamSettings
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed class RunEventStream(IRunStateStore store, RunEventHub hub, RunEventStreamSettings settings,
    IHostApplicationLifetime lifetime, ILogger<RunEventStream> logger)
{
    private const int PageSize = 256;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(HttpContext context, Guid runId, long cursor)
    {
        // Endpoint has checked existence. Register before the first historical read.
        await using var subscription = hub.Subscribe(runId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, subscription.Terminated, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var lastSent = cursor;
        var heartbeat = false;
        try
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await context.Response.StartAsync(ct);
            await context.Response.Body.FlushAsync(ct);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                while (subscription.Notifications.TryRead(out _)) { }
                var sent = false;
                while (true)
                {
                    var page = await store.ReadEventsAfterAsync(runId, lastSent, PageSize, ct);
                    foreach (var item in page)
                    {
                        var data = JsonSerializer.Serialize(SseEventDto.FromEvent(item), Json);
                        await context.Response.WriteAsync($"id: {item.Sequence.ToString(CultureInfo.InvariantCulture)}\nevent: {item.EventType}\ndata: {data}\n\n", ct);
                        await context.Response.Body.FlushAsync(ct);
                        lastSent = item.Sequence;
                        sent = true;
                        if (IsTerminal(item.EventType)) return;
                    }
                    if (page.Count < PageSize) break;
                }
                if (heartbeat && !sent)
                {
                    await context.Response.WriteAsync(": keepalive\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }

                // One cancellable wait at a time: no abandoned reads or growing timer/task list.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(settings.PollInterval);
                heartbeat = false;
                try
                {
                    if (!await subscription.Notifications.WaitToReadAsync(idle.Token)) return;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { heartbeat = true; } // Poll SQLite before deciding whether to send a comment.
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { context.Abort(); }
        catch (Exception exception) when (context.Response.HasStarted)
        {
            logger.LogWarning(exception, "Event stream ended for Run {RunId}", runId);
            context.Abort(); // Never append ProblemDetails to an SSE body.
        }
    }

    private static bool IsTerminal(ExecutionEventType type) => type is ExecutionEventType.RunCompleted
        or ExecutionEventType.RunFailed or ExecutionEventType.RunCancelled or ExecutionEventType.RunLimitReached;
}
