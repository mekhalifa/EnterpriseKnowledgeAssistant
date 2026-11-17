using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace EKA.Api.Streaming;

/// <summary>
/// Writes <c>text/event-stream</c> events to a response, starting the response only when the
/// first event is written (Step 13, ADR-0033).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why lazily, and why not <c>TypedResults.ServerSentEvents</c>.</b> The framework's result
/// commits the 200 and the headers before the first item is produced. Every failure that
/// happens before the model writes a word (conversation not found, budget spent, retrieval or
/// provider down) would then become "200, then an error event", and a client could no longer
/// tell a 404 from a 502 by status. Starting on the first event keeps those failures as
/// ordinary problem responses with their real status codes; only a failure <i>after</i> text has
/// been shown needs the in-band <c>error</c> event.
/// </para>
/// <para>
/// <b>Framing.</b> Each payload is serialized as one line of JSON, so the <c>data:</c> field never
/// needs splitting: a newline inside answer text is escaped as <c>\n</c> by the serializer. Each
/// event is flushed immediately, and response buffering is disabled, so a delta reaches the
/// browser when it is written rather than when a buffer fills. <c>X-Accel-Buffering: no</c> asks a
/// reverse proxy (nginx) to do the same.
/// </para>
/// </remarks>
internal sealed class ServerSentEventWriter(HttpResponse response, JsonSerializerOptions serializerOptions)
{
    public const string ContentType = "text/event-stream";

    /// <summary>True once the 200 and headers have been sent; the status can no longer change.</summary>
    public bool HasStarted { get; private set; }

    public async ValueTask WriteAsync<T>(string eventName, T payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        if (!HasStarted)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
        }

        var json = JsonSerializer.Serialize(payload, serializerOptions);
        var frame = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {json}\n\n");

        await response.Body.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await response.StartAsync(cancellationToken).ConfigureAwait(false);
        HasStarted = true;
    }
}
