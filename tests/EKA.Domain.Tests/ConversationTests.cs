using EKA.Domain.Common;
using EKA.Domain.Conversations;
using EKA.Domain.Ingestion;
using EKA.Domain.Retrieval;
using Shouldly;

namespace EKA.Domain.Tests;

public sealed class ConversationTests
{
    private static Conversation NewConversation() =>
        new(Guid.NewGuid(), TestData.Tenant, Guid.NewGuid(), TestData.Now);

    private static Message UserMessage(Conversation conversation, string content = "What is the leave policy?") =>
        Message.FromUser(Guid.NewGuid(), conversation.Id, content, TestData.Now);

    private static Message AssistantMessage(
        Conversation conversation,
        string content = "Employees receive 25 days.",
        IEnumerable<Citation>? citations = null) =>
        Message.FromAssistant(
            Guid.NewGuid(),
            conversation.Id,
            content,
            providerName: "azure-openai",
            modelName: "gpt-5",
            TestData.Now,
            citations);

    private static Citation Cite(Guid documentId, int chunkIndex, double score, int number = 1) =>
        new(documentId, "Employee Handbook", new ChunkIndex(chunkIndex), new SimilarityScore(score), number);

    // ── Construction ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_new_conversation_has_no_title_until_the_first_question()
    {
        var conversation = NewConversation();

        conversation.Title.ShouldBeNull();
        conversation.Messages.ShouldBeEmpty();
        conversation.IsArchived.ShouldBeFalse();
    }

    [Fact]
    public void A_conversation_must_belong_to_a_tenant_and_a_user()
    {
        Should.Throw<DomainException>(
            () => new Conversation(Guid.NewGuid(), default, Guid.NewGuid(), TestData.Now));

        Should.Throw<DomainException>(
            () => new Conversation(Guid.NewGuid(), TestData.Tenant, Guid.Empty, TestData.Now));
    }

    // ── Turn structure ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_conversation_cannot_start_with_an_assistant_message()
    {
        // An answer with no question is incoherent — and it is also how an injection
        // attempt would seed a thread with attacker-chosen "assistant" text that later
        // turns read as established context.
        var conversation = NewConversation();

        var act = () => conversation.AddAssistantMessage(AssistantMessage(conversation), TestData.Now);

        Should.Throw<DomainException>(act).Message.ShouldContain("cannot start with an assistant");
    }

    [Fact]
    public void An_assistant_message_cannot_follow_another_assistant_message()
    {
        var conversation = NewConversation();
        conversation.AddUserMessage(UserMessage(conversation), TestData.Now);
        conversation.AddAssistantMessage(AssistantMessage(conversation), TestData.Now);

        var act = () => conversation.AddAssistantMessage(AssistantMessage(conversation), TestData.Now);

        Should.Throw<DomainException>(act).Message.ShouldContain("must answer a user message");
    }

    [Fact]
    public void Alternating_turns_are_accepted()
    {
        var conversation = NewConversation();

        conversation.AddUserMessage(UserMessage(conversation), TestData.Now);
        conversation.AddAssistantMessage(AssistantMessage(conversation), TestData.Now);
        conversation.AddUserMessage(UserMessage(conversation, "And for part-time staff?"), TestData.Later);
        conversation.AddAssistantMessage(AssistantMessage(conversation, "Pro rata."), TestData.Later);

        conversation.Messages.Count.ShouldBe(4);
        conversation.Messages.Select(m => m.Role).ShouldBe(
        [
            MessageRole.User,
            MessageRole.Assistant,
            MessageRole.User,
            MessageRole.Assistant,
        ]);
    }

    [Fact]
    public void A_message_for_another_conversation_is_rejected()
    {
        var conversation = NewConversation();
        var other = NewConversation();

        var act = () => conversation.AddUserMessage(UserMessage(other), TestData.Now);

        Should.Throw<DomainException>(act).Message.ShouldContain("belongs to conversation");
    }

    [Fact]
    public void Adding_a_message_with_the_wrong_role_is_rejected()
    {
        var conversation = NewConversation();

        var act = () => conversation.AddUserMessage(AssistantMessage(conversation), TestData.Now);

        Should.Throw<DomainException>(act).Message.ShouldContain("Expected a User message");
    }

    // ── Title derivation ────────────────────────────────────────────────────────────

    [Fact]
    public void The_first_question_becomes_the_title()
    {
        var conversation = NewConversation();

        conversation.AddUserMessage(UserMessage(conversation, "What is the leave policy?"), TestData.Now);

        conversation.Title.ShouldBe("What is the leave policy?");
    }

    [Fact]
    public void Later_questions_do_not_overwrite_the_title()
    {
        var conversation = NewConversation();
        conversation.AddUserMessage(UserMessage(conversation, "First question"), TestData.Now);
        conversation.AddAssistantMessage(AssistantMessage(conversation), TestData.Now);

        conversation.AddUserMessage(UserMessage(conversation, "Second question"), TestData.Later);

        conversation.Title.ShouldBe("First question");
    }

    [Fact]
    public void A_long_question_is_truncated_for_the_title()
    {
        var conversation = NewConversation();
        var longQuestion = new string('x', Conversation.MaxTitleLength + 50);

        conversation.AddUserMessage(UserMessage(conversation, longQuestion), TestData.Now);

        conversation.Title!.Length.ShouldBe(Conversation.MaxTitleLength);
        conversation.Title.ShouldEndWith("…");
    }

    [Fact]
    public void Whitespace_in_a_title_is_collapsed()
    {
        var conversation = NewConversation();

        conversation.AddUserMessage(
            UserMessage(conversation, "What   is\n\tthe  policy?"), TestData.Now);

        conversation.Title.ShouldBe("What is the policy?");
    }

    // ── Archiving ───────────────────────────────────────────────────────────────────

    [Fact]
    public void An_archived_conversation_rejects_new_messages()
    {
        var conversation = NewConversation();
        conversation.AddUserMessage(UserMessage(conversation), TestData.Now);
        conversation.Archive(TestData.Later);

        var act = () => conversation.AddUserMessage(UserMessage(conversation), TestData.Later);

        Should.Throw<DomainException>(act).Message.ShouldContain("archived");
    }

    [Fact]
    public void Archiving_twice_is_rejected()
    {
        var conversation = NewConversation();
        conversation.Archive(TestData.Now);

        Should.Throw<InvalidStateTransitionException>(() => conversation.Archive(TestData.Later));
    }

    [Fact]
    public void Restoring_reopens_an_archived_conversation()
    {
        var conversation = NewConversation();
        conversation.Archive(TestData.Now);

        conversation.Restore(TestData.Later);

        conversation.IsArchived.ShouldBeFalse();
        Should.NotThrow(() => conversation.AddUserMessage(UserMessage(conversation), TestData.Later));
    }

    [Fact]
    public void Restoring_an_active_conversation_is_rejected()
    {
        Should.Throw<InvalidStateTransitionException>(
            () => NewConversation().Restore(TestData.Now));
    }

    // ── Messages ────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_user_message_carries_no_provider_or_model()
    {
        var conversation = NewConversation();

        var message = UserMessage(conversation);

        message.Role.ShouldBe(MessageRole.User);
        message.ProviderName.ShouldBeNull();
        message.ModelName.ShouldBeNull();
        message.Citations.ShouldBeEmpty();
    }

    [Fact]
    public void An_assistant_message_must_record_its_provider_and_model()
    {
        // Models change underneath you. When answer quality shifts, "which model wrote
        // this one?" is the first question and is unanswerable after the fact otherwise.
        var conversationId = Guid.NewGuid();

        Should.Throw<DomainException>(() => Message.FromAssistant(
            Guid.NewGuid(), conversationId, "answer", "  ", "gpt-5", TestData.Now));

        Should.Throw<DomainException>(() => Message.FromAssistant(
            Guid.NewGuid(), conversationId, "answer", "azure-openai", "", TestData.Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_message_cannot_be_empty(string content)
    {
        Should.Throw<DomainException>(
            () => Message.FromUser(Guid.NewGuid(), Guid.NewGuid(), content, TestData.Now));
    }

    [Fact]
    public void Citations_are_deduplicated_and_ordered_by_their_marker_not_their_score()
    {
        // The reader follows [1], [2] through the text; the source list reads in that order,
        // even when [2] scored higher than [1] (Step 10).
        var conversation = NewConversation();
        var documentId = Guid.NewGuid();

        var message = AssistantMessage(conversation, citations:
        [
            Cite(documentId, 3, 0.55, number: 1),
            Cite(documentId, 1, 0.95, number: 2),
            Cite(documentId, 3, 0.55, number: 1),
        ]);

        message.Citations.Select(c => (c.Number, c.ChunkIndex.Value)).ShouldBe([(1, 3), (2, 1)]);
    }

    [Fact]
    public void Chunks_from_different_documents_are_not_deduplicated()
    {
        var conversation = NewConversation();

        var message = AssistantMessage(conversation, citations:
        [
            Cite(Guid.NewGuid(), 0, 0.9, number: 1),
            Cite(Guid.NewGuid(), 0, 0.8, number: 2),
        ]);

        message.Citations.Count.ShouldBe(2);
    }

    [Fact]
    public void Two_different_sources_cannot_share_a_marker()
    {
        var conversation = NewConversation();

        Should.Throw<DomainException>(() => AssistantMessage(conversation, citations:
        [
            Cite(Guid.NewGuid(), 0, 0.9, number: 1),
            Cite(Guid.NewGuid(), 0, 0.8, number: 1),
        ]));
    }

    [Fact]
    public void A_prompt_template_version_starts_at_one()
    {
        Should.Throw<DomainException>(() => Message.FromAssistant(
            Guid.NewGuid(), Guid.NewGuid(), "a", "p", "m", TestData.Now, promptTemplateVersion: 0));
    }

    [Fact]
    public void A_citation_marker_starts_at_one()
    {
        Should.Throw<DomainException>(() => Cite(Guid.NewGuid(), 0, 0.9, number: 0));
    }

    [Fact]
    public void A_no_evidence_refusal_is_an_ungrounded_answer_that_names_no_model()
    {
        var conversation = NewConversation();

        var refusal = Message.NoEvidenceRefusal(Guid.NewGuid(), conversation.Id, "Not covered by your documents.", TestData.Now);

        refusal.Role.ShouldBe(MessageRole.Assistant);
        refusal.IsNoEvidenceRefusal.ShouldBeTrue();
        refusal.IsUngrounded.ShouldBeTrue();
        refusal.ModelName.ShouldBe(Message.NoEvidenceModel);
        AssistantMessage(conversation).IsNoEvidenceRefusal.ShouldBeFalse();
    }

    [Fact]
    public void An_assistant_message_without_citations_is_flagged_as_ungrounded()
    {
        var conversation = NewConversation();

        AssistantMessage(conversation).IsUngrounded.ShouldBeTrue();
        AssistantMessage(conversation, citations: [Cite(Guid.NewGuid(), 0, 0.9)])
            .IsUngrounded.ShouldBeFalse();

        // A user message is never "ungrounded" — the concept does not apply.
        UserMessage(conversation).IsUngrounded.ShouldBeFalse();
    }

    [Fact]
    public void A_citation_snapshots_the_document_title()
    {
        // Resolving the title live would let a later rename silently rewrite history, so
        // an old answer would claim it cited a document that never had that name.
        var citation = Cite(Guid.NewGuid(), 2, 0.8);

        citation.DocumentTitle.ShouldBe("Employee Handbook");
    }

    [Fact]
    public void A_citation_must_reference_a_document_and_record_its_title()
    {
        Should.Throw<DomainException>(
            () => new Citation(Guid.Empty, "Title", ChunkIndex.First, SimilarityScore.Exact, 1));

        Should.Throw<DomainException>(
            () => new Citation(Guid.NewGuid(), "  ", ChunkIndex.First, SimilarityScore.Exact, 1));
    }
}
