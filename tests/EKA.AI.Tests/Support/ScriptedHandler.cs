using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace EKA.AI.Tests.Support;

/// <summary>
/// Stands in for the network <b>below</b> the resilience pipeline: it replaces only the
/// primary handler of the named HttpClient, so every request still passes through the real
/// timeout, retry and circuit-breaker handlers and the real OpenAI SDK above them.
/// </summary>
/// <remarks>
/// That placement is the whole design. Mocking <c>IChatClient</c> would test our translation
/// code and nothing about whether retries happen, whether the SDK's own retries are really
/// off, or whether a hung connection times out — which are the behaviours this step adds.
/// </remarks>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _steps = new();
    private Func<CancellationToken, Task<HttpResponseMessage>>? _last;

    public List<string> RequestBodies { get; } = [];

    public List<Uri> RequestUris { get; } = [];

    public int Calls => RequestBodies.Count;

    /// <summary>Queue a response. The last step queued repeats once the queue is drained.</summary>
    public ScriptedHandler Then(HttpStatusCode status, string? json = null)
    {
        return Then(_ => Task.FromResult(Response(status, json)));
    }

    /// <summary>Queue a response computed from the request body, such as an embedding reply
    /// that must match the encoding the SDK asked for.</summary>
    public ScriptedHandler ThenFromRequest(Func<string, string> json)
    {
        return Then(_ => Task.FromResult(Response(HttpStatusCode.OK, json(RequestBodies[^1]))));
    }

    /// <summary>Queue a connection that never answers until cancelled.</summary>
    public ScriptedHandler ThenHang()
    {
        return Then(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
    }

    /// <summary>
    /// Queue a streamed chat reply (server-sent events, as OpenAI sends them), one chunk per
    /// delta, then usage, then <c>[DONE]</c>. With <paramref name="breakAfterDeltas"/> the
    /// connection drops after the deltas instead: what a provider outage mid-answer looks like.
    /// </summary>
    public ScriptedHandler ThenStream(string[] deltas, bool breakAfterDeltas = false)
    {
        return Then(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BreakingStream(Encoding.UTF8.GetBytes(ChatStream(deltas, breakAfterDeltas)), breakAfterDeltas))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") },
            },
        }));
    }

    private static string ChatStream(string[] deltas, bool truncated)
    {
        const string Head = """{"id":"chatcmpl-test","object":"chat.completion.chunk","created":1700000000,"model":"test-chat",""";
        var builder = new StringBuilder();

        foreach (var delta in deltas)
        {
            var escaped = System.Text.Json.JsonSerializer.Serialize(delta);
            builder.Append("data: ").Append(Head)
                .Append($$"""
                    "choices":[{"index":0,"delta":{"role":"assistant","content":{{escaped}}},"finish_reason":null}]}
                    """)
                .Append("\n\n");
        }

        if (!truncated)
        {
            builder.Append("data: ").Append(Head).Append("""
                "choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}
                """).Append("\n\n");
            builder.Append("data: ").Append(Head).Append("""
                "choices":[],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}
                """).Append("\n\n");
            builder.Append("data: [DONE]\n\n");
        }

        return builder.ToString();
    }

    /// <summary>Serves its bytes, then either ends or throws as a dropped connection does.</summary>
    private sealed class BreakingStream(byte[] content, bool breakAtEnd) : MemoryStream(content)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            Guard(base.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Guard(await base.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private static int Guard(int read, bool breakAtEnd) =>
            read == 0 && breakAtEnd ? throw new HttpIOException(HttpRequestError.ResponseEnded, "Connection dropped.") : read;

        private int Guard(int read) => Guard(read, breakAtEnd);
    }

    private ScriptedHandler Then(Func<CancellationToken, Task<HttpResponseMessage>> step)
    {
        _steps.Enqueue(step);
        _last = step;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestUris.Add(request.RequestUri!);
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));

        var step = _steps.Count > 0
            ? _steps.Dequeue()
            : _last ?? throw new InvalidOperationException("No response scripted.");

        return await step(cancellationToken);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string? json) => new(status)
    {
        Content = new StringContent(json ?? """{"error":{"message":"scripted"}}""", Encoding.UTF8, "application/json"),
    };

    public static string ChatJson(string content, string model = "test-chat") => $$"""
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "{{model}}",
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": "{{content}}" }, "finish_reason": "stop" }
          ],
          "usage": { "prompt_tokens": 12, "completion_tokens": 3, "total_tokens": 15 }
        }
        """;

    /// <summary>
    /// An embeddings reply with <paramref name="count"/> vectors of
    /// <paramref name="dimensions"/> components each. Honors the encoding the SDK requested:
    /// the OpenAI SDK asks for base64 to save bandwidth, so a test that always answered with a
    /// float array would pass against a client that real servers would confuse.
    /// </summary>
    public static string EmbeddingJson(string requestBody, int count, int dimensions)
    {
        var base64 = requestBody.Contains("\"base64\"", StringComparison.Ordinal);

        var items = Enumerable.Range(0, count).Select(index =>
        {
            // Distinct per index, so a test can tell whether order was preserved.
            var vector = Enumerable.Range(0, dimensions).Select(d => index + (d / 10f)).ToArray();
            var encoded = base64
                ? $"\"{Convert.ToBase64String(MemoryMarshal.AsBytes(vector.AsSpan()))}\""
                : $"[{string.Join(",", vector.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)))}]";

            return $$"""{ "object": "embedding", "index": {{index}}, "embedding": {{encoded}} }""";
        });

        return $$"""
            {
              "object": "list",
              "data": [ {{string.Join(",", items)}} ],
              "model": "test-embed",
              "usage": { "prompt_tokens": 7, "total_tokens": 7 }
            }
            """;
    }
}
