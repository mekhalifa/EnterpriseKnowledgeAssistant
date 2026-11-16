using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EKA.Application.Abstractions.AI;
using EKA.Contracts.Conversations;
using EKA.Domain.Conversations;
using Shouldly;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// Step 6's exit criterion end to end: a client creates a conversation, asks a question, and
/// reads back the persisted messages. Real HTTP, real middleware, real SQL Server, the
/// migration-seeded prompt template; only the model is a stand-in.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ConversationEndpointTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private EkaApiFactory _factory = null!;

    public ConversationEndpointTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public Task InitializeAsync()
    {
        _factory = new EkaApiFactory(_sqlServer.ConnectionString, vectorStore: _sqlServer.QdrantEndpoint);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<ConversationResponse> StartAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/conversations", content: null);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ConversationResponse>())!;
    }

    private static Task<HttpResponseMessage> AskAsync(HttpClient client, Guid conversationId, string question) =>
        client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages", new AskQuestionRequest(question));

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    // ── The exit criterion ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ask_and_read_back_the_persisted_messages()
    {
        var client = _factory.CreateClientFor(Guid.NewGuid());

        var created = await client.PostAsync("/api/conversations", content: null);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var conversation = (await created.Content.ReadFromJsonAsync<ConversationResponse>())!;
        conversation.Messages.ShouldBeEmpty();

        var asked = await AskAsync(client, conversation.Id, "How many days of annual leave do I get?");
        asked.StatusCode.ShouldBe(HttpStatusCode.OK);
        var exchange = (await asked.Content.ReadFromJsonAsync<AskQuestionResponse>())!;
        exchange.Answer.Content.ShouldBe("Answer to: How many days of annual leave do I get?");
        exchange.PromptTemplateVersion.ShouldBe(1);
        exchange.PromptTokens.ShouldBe(40);

        // Read back through the Location header: the contract a client actually follows.
        var readBack = await client.GetFromJsonAsync<ConversationResponse>(created.Headers.Location);

        readBack!.Title.ShouldBe("How many days of annual leave do I get?");
        readBack.Messages.Select(m => m.Role).ShouldBe([nameof(MessageRole.User), nameof(MessageRole.Assistant)]);
        readBack.Messages[0].Content.ShouldBe("How many days of annual leave do I get?");
        readBack.Messages[0].TokenCount.ShouldBeNull();
        readBack.Messages[1].TokenCount.ShouldBe(9);
        readBack.Messages[1].Model.ShouldBe("test-model");
    }

    [Fact]
    public async Task The_migration_seeded_template_is_what_the_model_is_instructed_with()
    {
        var client = _factory.CreateClientFor(Guid.NewGuid());
        var conversation = await StartAsync(client);

        // Status asserted, not assumed: this test once passed while the request behind it
        // was a 500 (step-06 section 5.1). The model is called before the save, so a
        // recorded prompt proves nothing about whether the exchange was persisted.
        (await AskAsync(client, conversation.Id, "hello")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var prompt = _factory.Chat.Prompts.Single();
        prompt[0].Role.ShouldBe(MessageRole.System);
        prompt[0].Content.ShouldContain("Enterprise Knowledge Assistant");
        prompt[0].Content.ShouldContain("Never invent");
        prompt[^1].ShouldBe(new ChatTurn(MessageRole.User, "hello"));
    }

    [Fact]
    public async Task Messages_come_back_in_conversation_order_and_feed_the_next_prompt_in_order()
    {
        // Guards the ordered Include. Without it SQL Server may return messages in any
        // order, and both the read-back and the prompt history would be shuffled.
        var client = _factory.CreateClientFor(Guid.NewGuid());
        var conversation = await StartAsync(client);

        for (var i = 1; i <= 4; i++)
        {
            (await AskAsync(client, conversation.Id, $"question {i}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var readBack = await client.GetFromJsonAsync<ConversationResponse>($"/api/conversations/{conversation.Id}");
        readBack!.Messages.Select(m => m.Content).ShouldBe(
        [
            "question 1", "Answer to: question 1",
            "question 2", "Answer to: question 2",
            "question 3", "Answer to: question 3",
            "question 4", "Answer to: question 4",
        ]);

        var lastPrompt = _factory.Chat.Prompts[^1];
        lastPrompt.Skip(1).Select(t => t.Content).ShouldBe(
        [
            "question 1", "Answer to: question 1",
            "question 2", "Answer to: question 2",
            "question 3", "Answer to: question 3",
            "question 4",
        ]);
    }

    // ── Provider failure ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_provider_outage_is_a_502_with_the_ai_code_and_leaves_no_half_turn()
    {
        var client = _factory.CreateClientFor(Guid.NewGuid());
        var conversation = await StartAsync(client);
        _factory.Chat.FailWith = AiErrors.Unavailable;

        var response = await AskAsync(client, conversation.Id, "Is anyone there?");

        // The first real use of ErrorType.Provider, end to end.
        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await ProblemAsync(response)).GetProperty("code").GetString().ShouldBe("ai.unavailable");

        var readBack = await client.GetFromJsonAsync<ConversationResponse>($"/api/conversations/{conversation.Id}");
        readBack!.Messages.ShouldBeEmpty();
    }

    // ── Isolation ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_colleague_in_the_same_tenant_cannot_read_or_post_into_my_conversation()
    {
        var tenant = Guid.NewGuid();
        var mine = _factory.CreateClientFor(tenant, "alice");
        var colleague = _factory.CreateClientFor(tenant, "bob");
        var conversation = await StartAsync(mine);

        (await colleague.GetAsync($"/api/conversations/{conversation.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AskAsync(colleague, conversation.Id, "peek")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _factory.Chat.Prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_same_subject_in_two_tenants_is_two_users_who_cannot_see_each_other()
    {
        // Under the global UX_Users_ExternalId index this test's second request failed: the
        // second tenant's "dev-user" collided with the first's. With the global lookup it
        // would have been worse, and resolved to the other tenant's user.
        var tenantA = _factory.CreateClientFor(Guid.NewGuid(), "same-subject");
        var tenantB = _factory.CreateClientFor(Guid.NewGuid(), "same-subject");

        var inA = await StartAsync(tenantA);
        var inB = await StartAsync(tenantB);

        (await tenantB.GetAsync($"/api/conversations/{inA.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var listedInB = await tenantB.GetFromJsonAsync<List<ConversationSummaryResponse>>("/api/conversations");
        listedInB!.Select(c => c.Id).ShouldBe([inB.Id]);
    }

    [Fact]
    public async Task Reading_before_ever_writing_is_an_empty_list_not_an_error()
    {
        var client = _factory.CreateClientFor(Guid.NewGuid(), "brand-new");

        var listed = await client.GetFromJsonAsync<List<ConversationSummaryResponse>>("/api/conversations");

        listed.ShouldBeEmpty();
    }

    // ── Edge rejections ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_empty_question_is_rejected_at_the_edge()
    {
        var client = _factory.CreateClientFor(Guid.NewGuid());
        var conversation = await StartAsync(client);

        var response = await AskAsync(client, conversation.Id, "  ");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _factory.Chat.Prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Asking_in_a_conversation_that_does_not_exist_is_a_404_with_its_code()
    {
        var client = _factory.CreateClientFor(Guid.NewGuid());

        var response = await AskAsync(client, Guid.NewGuid(), "hello?");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ProblemAsync(response)).GetProperty("code").GetString().ShouldBe("conversation.not_found");
    }
}
