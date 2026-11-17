using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EKA.Application.Abstractions.AI;
using EKA.Contracts.Conversations;
using Shouldly;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// Step 13: <c>POST /api/conversations/{id}/messages/stream</c> over the real pipeline. The
/// contract the Angular client is written against (ADR-0033): deltas, then exactly one
/// <c>completed</c> or <c>error</c>; failures before the first delta keep their HTTP status.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class AnswerStreamingTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private EkaApiFactory _factory = null!;

    public AnswerStreamingTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public Task InitializeAsync()
    {
        _factory = new EkaApiFactory(
            _sqlServer.ConnectionString,
            new Dictionary<string, string?> { ["VectorStore:CollectionName"] = $"streaming_{Guid.NewGuid():N}" },
            _sqlServer.QdrantEndpoint);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<HttpClient> ClientWithEvidenceAsync()
    {
        var tenant = Guid.NewGuid();
        await SearchableDocuments.SeedAsync(_sqlServer, _factory, tenant, "Employees receive 25 days of annual leave.");
        return _factory.CreateClientFor(tenant);
    }

    private static async Task<Guid> StartAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/conversations", content: null);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ConversationResponse>())!.Id;
    }

    private static Task<HttpResponseMessage> StreamAsync(HttpClient client, Guid conversationId, string question)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{conversationId}/messages/stream")
        {
            Content = JsonContent.Create(new AskQuestionRequest(question)),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    /// <summary>Parses a complete <c>text/event-stream</c> body into (event, data) pairs.</summary>
    private static async Task<List<(string Event, JsonElement Data)>> EventsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var events = new List<(string, JsonElement)>();

        foreach (var frame in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = frame.Split('\n');
            var name = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
            var data = lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
            events.Add((name, JsonDocument.Parse(data).RootElement.Clone()));
        }

        return events;
    }

    [Fact]
    public async Task An_answer_streams_as_deltas_then_one_completed_event_with_the_persisted_exchange()
    {
        var client = await ClientWithEvidenceAsync();
        var conversationId = await StartAsync(client);
        _factory.Chat.Reply = "You get 25 days [1].";

        var response = await StreamAsync(client, conversationId, "How much leave?");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        response.Headers.CacheControl!.NoCache.ShouldBeTrue();

        var events = await EventsAsync(response);
        events.SkipLast(1).ShouldAllBe(e => e.Event == AnswerStreamEvents.Delta);
        string.Concat(events.SkipLast(1).Select(e => e.Data.GetProperty("text").GetString()))
            .ShouldBe("You get 25 days [1].");

        var (lastEvent, completed) = events[^1];
        lastEvent.ShouldBe(AnswerStreamEvents.Completed);
        completed.GetProperty("grounding").GetString().ShouldBe("cited");
        completed.GetProperty("answer").GetProperty("citations")[0].GetProperty("documentTitle").GetString().ShouldBe("Leave policy");

        // Persisted exactly as the non-streaming endpoint would have.
        var readBack = await client.GetFromJsonAsync<ConversationResponse>($"/api/conversations/{conversationId}");
        readBack!.Messages.Select(m => m.Content).ShouldBe(["How much leave?", "You get 25 days [1]."]);
    }

    [Fact]
    public async Task A_failure_before_any_text_keeps_its_http_status_instead_of_starting_a_stream()
    {
        var client = await ClientWithEvidenceAsync();
        var conversationId = await StartAsync(client);
        _factory.Chat.FailWith = AiErrors.Unavailable;
        _factory.Chat.DeltasBeforeFailure = 0;

        var response = await StreamAsync(client, conversationId, "Anyone there?");

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("code").GetString().ShouldBe(AiErrors.Unavailable.Code);
    }

    [Fact]
    public async Task Another_users_conversation_is_a_404_not_an_empty_stream()
    {
        var client = await ClientWithEvidenceAsync();
        var conversationId = await StartAsync(client);

        var response = await StreamAsync(_factory.CreateClientFor(Guid.NewGuid()), conversationId, "peek");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _factory.Chat.Prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_question_is_refused_by_the_same_validation_as_the_json_endpoint()
    {
        var client = await ClientWithEvidenceAsync();
        var conversationId = await StartAsync(client);

        var response = await StreamAsync(client, conversationId, "   ");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_provider_failing_mid_answer_ends_the_stream_with_an_error_event_and_persists_nothing()
    {
        var client = await ClientWithEvidenceAsync();
        var conversationId = await StartAsync(client);
        _factory.Chat.Reply = "You get twenty five days of leave.";
        _factory.Chat.FailWith = AiErrors.Unavailable;
        _factory.Chat.DeltasBeforeFailure = 3;

        var response = await StreamAsync(client, conversationId, "How much leave?");

        // The 200 was already sent with the first delta; the failure has to travel in-band.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var events = await EventsAsync(response);
        events.Select(e => e.Event).ShouldBe(
            [AnswerStreamEvents.Delta, AnswerStreamEvents.Delta, AnswerStreamEvents.Delta, AnswerStreamEvents.Error]);

        var problem = events[^1].Data;
        problem.GetProperty("status").GetInt32().ShouldBe(502);
        problem.GetProperty("code").GetString().ShouldBe(AiErrors.Unavailable.Code);
        problem.TryGetProperty("traceId", out _).ShouldBeTrue();

        var readBack = await client.GetFromJsonAsync<ConversationResponse>($"/api/conversations/{conversationId}");
        readBack!.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_no_evidence_refusal_is_a_stream_of_one_completed_event_and_no_model_call()
    {
        var client = _factory.CreateClientFor(Guid.NewGuid()); // a tenant with no documents
        var conversationId = await StartAsync(client);

        var response = await StreamAsync(client, conversationId, "What is the capital of Australia?");

        var events = await EventsAsync(response);
        events.ShouldHaveSingleItem().Event.ShouldBe(AnswerStreamEvents.Completed);
        events[0].Data.GetProperty("grounding").GetString().ShouldBe("no_evidence");
        _factory.Chat.Prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Answer_text_with_newlines_and_markup_survives_framing_as_data_not_markup()
    {
        // One JSON line per data field: a newline in the answer must not end the event early, and
        // markup arrives as characters for the client to render as text (threat model LLM05).
        var client = await ClientWithEvidenceAsync();
        var conversationId = await StartAsync(client);
        _factory.Chat.Reply = "Line one.\n\ndata: forged\n\n<img src=x onerror=alert(1)> [1]";

        var events = await EventsAsync(await StreamAsync(client, conversationId, "q"));

        events[^1].Event.ShouldBe(AnswerStreamEvents.Completed);
        string.Concat(events.SkipLast(1).Select(e => e.Data.GetProperty("text").GetString()))
            .ShouldBe("Line one.\n\ndata: forged\n\n<img src=x onerror=alert(1)> [1]");
    }
}
