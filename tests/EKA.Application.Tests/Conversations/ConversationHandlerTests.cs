using EKA.Application.Abstractions.AI;
using EKA.Application.Conversations;
using EKA.Application.Conversations.AskQuestion;
using EKA.Application.Conversations.GetConversation;
using EKA.Application.Conversations.StartConversation;
using EKA.Application.Identity;
using EKA.Application.Retrieval;
using EKA.Application.Tests.Fakes;
using EKA.Domain.Conversations;
using EKA.Domain.Prompting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace EKA.Application.Tests.Conversations;

public sealed class ConversationHandlerTests
{
    private readonly FakeUserRepository _users = new();
    private readonly FakeConversationRepository _conversations = new();
    private readonly FakePromptTemplateRepository _templates = new();
    private readonly FakeChatCompletionService _chat = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FixedTimeProvider _time = FixedTimeProvider.Default;

    // Retrieval, real retriever over in-memory stores. Every question embeds as [1,0,0,0], so a
    // chunk indexed with Same scores 1.0 and is always found; Far (cosine 0) is under the floor.
    private static readonly float[] Same = [1, 0, 0, 0];
    private readonly FakeVectorStore _vectors = new();
    private readonly FakeChunkReader _chunks = new();
    private readonly FixedQueryEmbedding _embeddings = new([1, 0, 0, 0]);

    private static readonly CallerIdentity Alice = new(TestData.Tenant, "alice", "alice@example.test", "Alice");

    private const string GroundedTemplate = "Sources:\n{{context}}\n\nQuestion: {{question}}";

    public ConversationHandlerTests()
    {
        var template = new PromptTemplate(
            Guid.NewGuid(), "default", 2, "Be careful.", GroundedTemplate, Guid.NewGuid(), FixedTimeProvider.DefaultNow);
        template.Activate(FixedTimeProvider.DefaultNow);
        _templates.Add(template);

        // Alice's tenant has one tenant-wide policy, so ordinary questions find evidence.
        Index(TestData.Completed(chunkTexts: "Employees receive 25 days of annual leave."));
    }

    private Domain.Ingestion.Document Index(Domain.Ingestion.Document document, float[]? vector = null)
    {
        _chunks.With(document);
        foreach (var chunk in document.Chunks)
        {
            _vectors.Seed(new Abstractions.Ingestion.ChunkVector(
                document.TenantId, document.Id, chunk.Index, vector ?? Same, _embeddings.Model, chunk.PageRange, chunk.SectionPath, document.AccessKeys));
        }

        return document;
    }

    private UserProvisioner Provisioner() => new(_users, _unitOfWork, _time);

    private StartConversationHandler Start() => new(Provisioner(), _conversations, _unitOfWork, _time);

    private readonly FakeTokenUsageLedger _ledger = new();
    private long _monthlyBudget = 1_000_000;

    private ChunkRetriever Retriever() =>
        new(_embeddings, _vectors, _chunks, Budgets.For(_ledger, _monthlyBudget), Options.Create(new RetrievalOptions()), NullLogger<ChunkRetriever>.Instance);

    private AskQuestionHandler Ask(int maxPromptTokens = 6000, int maxContextTokens = 3000) => new(
        Provisioner(), _conversations, _templates, Retriever(), Budgets.For(_ledger, _monthlyBudget), _chat,
        new PromptBuilder(new EstimatingTokenCounter()), _unitOfWork,
        Options.Create(new ChatOptions { MaxPromptTokens = maxPromptTokens, MaxContextTokens = maxContextTokens }), _time);

    private async Task<Guid> StartedConversation(CallerIdentity? caller = null) =>
        (await Start().HandleAsync(new StartConversationCommand(caller ?? Alice))).Value.Id;

    // ── Provisioning ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task First_contact_provisions_a_user_and_later_contact_reuses_it()
    {
        await StartedConversation();
        await StartedConversation();

        _users.All.Count.ShouldBe(1);
        _users.All[0].Email.ShouldBe("alice@example.test");
    }

    [Fact]
    public async Task The_same_subject_in_another_tenant_is_a_different_user()
    {
        // The bug Step 6 fixed: a global subject lookup would hand Bob-in-tenant-B the user
        // row of whoever first presented that subject in tenant A.
        await StartedConversation(Alice);
        await StartedConversation(Alice with { TenantId = TestData.OtherTenant });

        _users.All.Count.ShouldBe(2);
        _users.All.Select(u => u.TenantId).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task A_caller_without_an_email_is_refused_rather_than_given_an_invented_one()
    {
        var result = await Start().HandleAsync(new StartConversationCommand(Alice with { Email = null }));

        result.Error.Code.ShouldBe("user.incomplete_identity");
        _users.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_display_name_falls_back_to_the_email()
    {
        await StartedConversation(Alice with { DisplayName = " " });

        _users.All[0].DisplayName.ShouldBe("alice@example.test");
    }

    [Fact]
    public async Task A_deactivated_user_cannot_start_a_conversation()
    {
        await StartedConversation();
        _users.All[0].Deactivate(FixedTimeProvider.DefaultNow);

        var result = await Start().HandleAsync(new StartConversationCommand(Alice));

        result.Error.Code.ShouldBe("user.deactivated");
    }

    [Fact]
    public async Task Reading_never_provisions()
    {
        var handler = new GetConversationHandler(Provisioner(), _conversations);

        var result = await handler.HandleAsync(new GetConversationQuery(Alice, Guid.NewGuid()));

        result.Error.Code.ShouldBe("conversation.not_found");
        _users.All.ShouldBeEmpty();
        _unitOfWork.SaveCount.ShouldBe(0);
    }

    // ── Asking ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Asking_persists_the_question_and_the_answer_together()
    {
        var conversationId = await StartedConversation();
        var savesBefore = _unitOfWork.SaveCount;

        var result = await Ask().HandleAsync(new AskQuestionCommand(Alice, conversationId, "How many leave days?"));

        result.IsSuccess.ShouldBeTrue(result.ToString());
        var conversation = _conversations.All.Single();
        conversation.Messages.Select(m => m.Role).ShouldBe([MessageRole.User, MessageRole.Assistant]);
        conversation.Title.ShouldBe("How many leave days?");
        _unitOfWork.SaveCount.ShouldBe(savesBefore + 1);

        result.Value.Answer.TokenCount.ShouldBe(7);
        result.Value.Answer.Model.ShouldBe("fake-model");
        result.Value.PromptTokens.ShouldBe(50);
        result.Value.PromptTemplateVersion.ShouldBe(2);
    }

    [Fact]
    public async Task A_provider_failure_returns_its_error_and_persists_nothing()
    {
        var conversationId = await StartedConversation();
        var savesBefore = _unitOfWork.SaveCount;
        _chat.FailWith = AiErrors.Unavailable;

        var result = await Ask().HandleAsync(new AskQuestionCommand(Alice, conversationId, "Anyone there?"));

        result.Error.ShouldBe(AiErrors.Unavailable);
        _conversations.All.Single().Messages.ShouldBeEmpty();
        _unitOfWork.SaveCount.ShouldBe(savesBefore);
    }

    [Fact]
    public async Task The_second_question_carries_the_first_exchange_as_history()
    {
        var conversationId = await StartedConversation();
        await Ask().HandleAsync(new AskQuestionCommand(Alice, conversationId, "first"));

        await Ask().HandleAsync(new AskQuestionCommand(Alice, conversationId, "second"));

        var prompt = _chat.Prompts[^1];
        prompt.Select(t => t.Role).ShouldBe([MessageRole.System, MessageRole.User, MessageRole.Assistant, MessageRole.User]);
        prompt[1].Content.ShouldBe("first");          // history is the question as asked,
        prompt[2].Content.ShouldBe("An answer.");     // not the sources it came with
        prompt[3].Content.ShouldEndWith("Question: second");
    }

    [Fact]
    public async Task Another_users_conversation_is_not_found()
    {
        var conversationId = await StartedConversation(Alice);
        var bob = Alice with { SubjectId = "bob", Email = "bob@example.test" };

        var result = await Ask().HandleAsync(new AskQuestionCommand(bob, conversationId, "peek"));

        result.Error.Code.ShouldBe("conversation.not_found");
        _chat.Prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_oversized_question_never_reaches_the_provider()
    {
        var conversationId = await StartedConversation();

        var result = await Ask(maxPromptTokens: 300)
            .HandleAsync(new AskQuestionCommand(Alice, conversationId, new string('x', 4000)));

        result.Error.Code.ShouldBe("conversation.question_too_long");
        _chat.Prompts.ShouldBeEmpty();
    }

    // ── Streaming (Step 13) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_streamed_answer_shows_the_raw_text_and_persists_the_renumbered_answer_once()
    {
        Index(TestData.Completed(chunkTexts: "Carry-over is five days."));
        _chat.Answer = "Carry-over is five days [2].";
        var conversationId = await StartedConversation();
        var savesBefore = _unitOfWork.SaveCount;
        var deltas = new List<string>();

        var result = await Ask().StreamAsync(
            new AskQuestionCommand(Alice, conversationId, "Carry-over?"),
            (delta, _) => { deltas.Add(delta); return ValueTask.CompletedTask; });

        result.IsSuccess.ShouldBeTrue(result.ToString());
        // What the user watched arrive: the model's own words and numbering…
        string.Concat(deltas).ShouldBe("Carry-over is five days [2].");
        // …and what is kept: renumbered, cited, saved once with the question.
        result.Value.Answer.Content.ShouldBe("Carry-over is five days [1].");
        result.Value.Answer.Citations.ShouldHaveSingleItem().Number.ShouldBe(1);
        _unitOfWork.SaveCount.ShouldBe(savesBefore + 1);
        _conversations.All.Single().Messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_provider_failing_mid_stream_persists_nothing_although_text_was_shown()
    {
        var conversationId = await StartedConversation();
        var savesBefore = _unitOfWork.SaveCount;
        _chat.Answer = "You get twenty five days.";
        _chat.FailWith = AiErrors.Unavailable;
        _chat.DeltasBeforeFailure = 2;
        var deltas = new List<string>();

        var result = await Ask().StreamAsync(
            new AskQuestionCommand(Alice, conversationId, "Leave?"),
            (delta, _) => { deltas.Add(delta); return ValueTask.CompletedTask; });

        deltas.Count.ShouldBe(2);
        result.Error.ShouldBe(AiErrors.Unavailable);
        // The same atomic turn as without streaming: half an answer is not an answer.
        _conversations.All.Single().Messages.ShouldBeEmpty();
        _unitOfWork.SaveCount.ShouldBe(savesBefore);
    }

    [Fact]
    public async Task A_streamed_no_evidence_refusal_emits_no_deltas_because_no_model_was_asked()
    {
        var other = Alice with { TenantId = TestData.OtherTenant };
        var conversationId = await StartedConversation(other);
        var deltas = new List<string>();

        var result = await Ask().StreamAsync(
            new AskQuestionCommand(other, conversationId, "Anything?"),
            (delta, _) => { deltas.Add(delta); return ValueTask.CompletedTask; });

        result.Value.Grounding.ShouldBe("no_evidence");
        deltas.ShouldBeEmpty();
        _chat.Prompts.ShouldBeEmpty();
    }

    // ── Grounding (Step 10) ─────────────────────────────────────────────────────────

    private async Task<Contracts.Conversations.AskQuestionResponse> AskAsync(string question, CallerIdentity? caller = null)
    {
        var conversationId = await StartedConversation(caller);
        var result = await Ask().HandleAsync(new AskQuestionCommand(caller ?? Alice, conversationId, question));
        result.IsSuccess.ShouldBeTrue(result.ToString());
        return result.Value;
    }

    [Fact]
    public async Task The_sources_reach_the_question_turn_numbered_and_never_the_system_turn()
    {
        await AskAsync("How many days of leave?");

        var prompt = _chat.Prompts.Single();
        prompt[0].Content.ShouldBe("Be careful.");
        prompt[^1].Content.ShouldContain("<source id=\"1\" title=\"Employee handbook\" section=\"Leave\">");
        prompt[^1].Content.ShouldContain("Employees receive 25 days of annual leave.");
        prompt[^1].Content.ShouldEndWith("Question: How many days of leave?");
    }

    [Fact]
    public async Task An_answer_citing_its_source_is_stored_with_that_citation()
    {
        _chat.Answer = "You get 25 days [1].";

        var response = await AskAsync("How many days of leave?");

        response.Grounding.ShouldBe("cited");
        response.Answer.Content.ShouldBe("You get 25 days [1].");
        var citation = response.Answer.Citations.ShouldHaveSingleItem();
        citation.Number.ShouldBe(1);
        citation.DocumentTitle.ShouldBe("Employee handbook");
        citation.SectionPath.ShouldBe("Leave");
        citation.Score.ShouldBe(1.0);

        var stored = _conversations.All.Single().Messages.Last();
        stored.IsUngrounded.ShouldBeFalse();
        stored.Citations.ShouldHaveSingleItem().Number.ShouldBe(1);
        stored.PromptTemplateVersion.ShouldBe(2);
    }

    [Fact]
    public async Task A_refusal_records_no_template_version_because_no_prompt_was_sent()
    {
        await AskAsync("Anything?", Alice with { TenantId = TestData.OtherTenant });

        _conversations.All.Single().Messages.Last().PromptTemplateVersion.ShouldBeNull();
    }

    [Fact]
    public async Task Citations_are_renumbered_by_first_appearance_and_invented_ones_dropped()
    {
        Index(TestData.Completed(chunkTexts: "Sick leave needs a certificate."));
        Index(TestData.Completed(chunkTexts: "Carry-over is five days."));
        _chat.Answer = "Carry-over is five days [3]. Annual leave is 25 days [1, 9]. See also [7].";

        var response = await AskAsync("Leave rules?");

        response.Answer.Content.ShouldBe("Carry-over is five days [1]. Annual leave is 25 days [2]. See also.");
        response.Answer.Citations.Select(c => c.Number).ShouldBe([1, 2]);
        response.SourcesProvided.ShouldBe(3);
    }

    [Fact]
    public async Task With_no_relevant_source_the_system_refuses_without_calling_the_model()
    {
        var response = await AskAsync("What is the capital of Australia?", Alice with { TenantId = TestData.OtherTenant });

        response.Grounding.ShouldBe("no_evidence");
        response.Answer.Model.ShouldBe(Message.NoEvidenceModel);
        response.Answer.Content.ShouldBe(new ChatOptions().NoEvidenceAnswer);
        response.Answer.Citations.ShouldBeEmpty();
        response.PromptTokens.ShouldBeNull();
        _chat.Prompts.ShouldBeEmpty();

        // The exchange is still recorded: the question was asked and answered.
        _conversations.All.Single().Messages.Last().IsNoEvidenceRefusal.ShouldBeTrue();
    }

    [Fact]
    public async Task A_restricted_document_never_reaches_a_non_members_prompt()
    {
        // The only document in this tenant about salaries is HR-only. Bob is not in HR.
        var otherTenant = TestData.OtherTenant;
        Index(TestData.Completed(otherTenant, Domain.Access.DocumentAccess.Of([Domain.Access.AccessPrincipal.Group("hr")]), chunkTexts: "Salary band C is 60k."));
        var bob = new CallerIdentity(otherTenant, "bob", "bob@example.test", "Bob");
        var carol = new CallerIdentity(otherTenant, "carol", "carol@example.test", "Carol", ["hr"]);

        (await AskAsync("What is band C?", bob)).Grounding.ShouldBe("no_evidence");
        _chat.Prompts.ShouldBeEmpty();

        await AskAsync("What is band C?", carol);
        _chat.Prompts.Single()[^1].Content.ShouldContain("Salary band C is 60k.");
    }

    [Fact]
    public async Task A_model_that_cites_nothing_is_stored_as_an_ungrounded_answer()
    {
        _chat.Answer = "The available documents do not cover parking.";

        var response = await AskAsync("Where do I park?");

        response.Grounding.ShouldBe("uncited");
        response.Answer.Citations.ShouldBeEmpty();
        _conversations.All.Single().Messages.Last().IsUngrounded.ShouldBeTrue();
    }

    [Fact]
    public async Task Earlier_answers_lose_their_markers_before_they_are_replayed_as_history()
    {
        _chat.Answer = "You get 25 days [1].";
        var conversationId = await StartedConversation();
        await Ask().HandleAsync(new AskQuestionCommand(Alice, conversationId, "first"));

        await Ask().HandleAsync(new AskQuestionCommand(Alice, conversationId, "second"));

        // "[1]" referred to the first turn's source 1, not to this turn's.
        _chat.Prompts[^1][2].Content.ShouldBe("You get 25 days.");
        _conversations.All.Single().Messages.ElementAt(1).Content.ShouldBe("You get 25 days [1].");
    }

    [Fact]
    public async Task A_retrieval_failure_is_reported_and_nothing_is_persisted()
    {
        var conversationId = await StartedConversation();
        var savesBefore = _unitOfWork.SaveCount;
        _embeddings.FailWith = AiErrors.Timeout;

        var result = await Ask().HandleAsync(new AskQuestionCommand(Alice, conversationId, "leave?"));

        result.Error.ShouldBe(AiErrors.Timeout);
        _conversations.All.Single().Messages.ShouldBeEmpty();
        _unitOfWork.SaveCount.ShouldBe(savesBefore);
        _chat.Prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sources_that_do_not_fit_the_context_budget_are_left_out_whole_and_cannot_be_cited()
    {
        Index(TestData.Completed(chunkTexts: new string('x', 4000)));
        _chat.Answer = "Twenty-five days [1]. Also [2].";
        var conversationId = await StartedConversation();

        var result = await Ask(maxContextTokens: 200).HandleAsync(new AskQuestionCommand(Alice, conversationId, "leave?"));

        result.Value.SourcesRetrieved.ShouldBe(2);
        result.Value.SourcesProvided.ShouldBe(1);
        result.Value.Answer.Citations.ShouldHaveSingleItem();
        _chat.Prompts.Single()[^1].Content.ShouldNotContain("xxxx");
    }

    // ── Token budget (Step 12) ──────────────────────────────────────────────────────

    [Fact]
    public async Task An_answered_question_records_what_it_cost_against_the_tenant()
    {
        await AskAsync("How many days of leave?");

        var spent = _ledger.For(TestData.Tenant);
        spent.ChatInputTokens.ShouldBe(50);   // the provider's count (FakeChatCompletionService)
        spent.ChatOutputTokens.ShouldBe(7);
        spent.EmbeddingTokens.ShouldBe(1);    // the question's embedding (FixedQueryEmbedding reports 1)
    }

    [Fact]
    public async Task A_spent_budget_stops_the_question_before_any_provider_is_called()
    {
        _monthlyBudget = 100;
        await _ledger.AddAsync(TestData.Tenant, Abstractions.Usage.UsagePeriod.Of(FixedTimeProvider.DefaultNow), new(80, 20, 0));
        var conversationId = await StartedConversation();
        var savesBefore = _unitOfWork.SaveCount;

        var result = await Ask().HandleAsync(new AskQuestionCommand(Alice, conversationId, "leave?"));

        result.Error.Code.ShouldBe("usage.budget_exhausted");
        result.Error.Type.ShouldBe(EKA.Application.Common.ErrorType.LimitExceeded);
        _embeddings.Calls.ShouldBe(0);
        _chat.Prompts.ShouldBeEmpty();
        _unitOfWork.SaveCount.ShouldBe(savesBefore);
    }

    [Fact]
    public async Task Budgets_are_per_tenant()
    {
        _monthlyBudget = 100;
        await _ledger.AddAsync(TestData.OtherTenant, Abstractions.Usage.UsagePeriod.Of(FixedTimeProvider.DefaultNow), new(100, 0, 0));

        // AskAsync asserts success; the model being called proves the budget let it through.
        (await AskAsync("leave?")).Answer.Model.ShouldBe("fake-model", "another tenant's spending must not stop this one");
    }

    [Fact]
    public async Task A_ledger_outage_does_not_fail_an_answer_that_was_already_paid_for()
    {
        _ledger.FailWrites = true;

        (await AskAsync("leave?")).Answer.Content.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_template_without_a_context_placeholder_is_refused_for_grounded_answers()
    {
        var ungrounded = new PromptTemplate(Guid.NewGuid(), "plain", 1, "s", "{{question}}", Guid.NewGuid(), FixedTimeProvider.DefaultNow);
        var chunk = new RetrievedChunk(
            new Abstractions.Retrieval.ChunkPassage(Guid.NewGuid(), "t", Domain.Ingestion.ChunkIndex.First, "c", null, null),
            Domain.Retrieval.SimilarityScore.Exact);

        Should.Throw<InvalidOperationException>(() => new PromptBuilder(new EstimatingTokenCounter())
            .WithTemplate(ungrounded).WithQuestion("q").WithSources([chunk], 1000).WithBudget(6000, 20).Build());
    }
}
