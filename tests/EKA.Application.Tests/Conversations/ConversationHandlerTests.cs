using EKA.Application.Abstractions.AI;
using EKA.Application.Conversations;
using EKA.Application.Conversations.AskQuestion;
using EKA.Application.Conversations.GetConversation;
using EKA.Application.Conversations.StartConversation;
using EKA.Application.Identity;
using EKA.Application.Tests.Fakes;
using EKA.Domain.Conversations;
using EKA.Domain.Prompting;
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

    private static readonly CallerIdentity Alice = new(TestData.Tenant, "alice", "alice@example.test", "Alice");

    public ConversationHandlerTests()
    {
        var template = new PromptTemplate(
            Guid.NewGuid(), "default", 1, "Be careful.", "{{question}}", Guid.NewGuid(), FixedTimeProvider.DefaultNow);
        template.Activate(FixedTimeProvider.DefaultNow);
        _templates.Add(template);
    }

    private UserProvisioner Provisioner() => new(_users, _unitOfWork, _time);

    private StartConversationHandler Start() => new(Provisioner(), _conversations, _unitOfWork, _time);

    private AskQuestionHandler Ask(int maxPromptTokens = 6000) => new(
        Provisioner(), _conversations, _templates, _chat,
        new PromptBuilder(new EstimatingTokenCounter()), _unitOfWork,
        Options.Create(new ChatOptions { MaxPromptTokens = maxPromptTokens }), _time);

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
        result.Value.PromptTemplateVersion.ShouldBe(1);
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

        _chat.Prompts[^1].Select(t => (t.Role, t.Content)).ShouldBe(
        [
            (MessageRole.System, "Be careful."),
            (MessageRole.User, "first"),
            (MessageRole.Assistant, "An answer."),
            (MessageRole.User, "second"),
        ]);
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
}
