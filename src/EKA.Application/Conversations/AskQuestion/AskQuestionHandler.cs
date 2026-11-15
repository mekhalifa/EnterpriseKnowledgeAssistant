using EKA.Application.Abstractions;
using EKA.Application.Abstractions.AI;
using EKA.Application.Common;
using EKA.Application.Identity;
using EKA.Contracts.Conversations;
using EKA.Domain.Conversations;
using Microsoft.Extensions.Options;

namespace EKA.Application.Conversations.AskQuestion;

/// <summary>
/// Asks the model a question in the context of a conversation, and records the exchange.
/// </summary>
/// <remarks>
/// <para>
/// <b>A turn is atomic: question and answer are persisted together, or neither is.</b>
/// The model is called <i>before</i> the aggregate is touched. If the provider fails, the
/// handler returns the 502 with nothing written, and the caller retries the same
/// question. The alternative (save the question, then call) leaves an unanswered question
/// in the thread after every outage. The next prompt would replay it as history, and the
/// model would answer two questions at once.
/// </para>
/// <para>
/// The cost of this order is that a question is not durable until it has been answered.
/// Losing an unanswered question in an outage is the lesser harm: the user still has it
/// on screen.
/// </para>
/// <para>
/// No database transaction is open during the model call: EF Core opens a connection per
/// query and per <c>SaveChanges</c>, so a slow model holds no locks.
/// </para>
/// </remarks>
public sealed class AskQuestionHandler(
    UserProvisioner provisioner,
    IConversationRepository conversations,
    IPromptTemplateRepository templates,
    IChatCompletionService chat,
    PromptBuilder promptBuilder,
    IUnitOfWork unitOfWork,
    IOptions<ChatOptions> options,
    TimeProvider timeProvider)
{
    private readonly ChatOptions _options = options.Value;

    public async Task<Result<AskQuestionResponse>> HandleAsync(
        AskQuestionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var user = await provisioner.GetOrProvisionAsync(command.Caller, cancellationToken).ConfigureAwait(false);
        if (user.IsFailure)
        {
            return user.Error;
        }

        var conversation = await conversations
            .GetWithMessagesAsync(command.Caller.TenantId, user.Value.Id, command.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId);
        }

        // A missing active template is not the caller's fault and not an expected failure.
        // The migration seeds one, so reaching here means the database is behind the code,
        // and that should be a loud 500.
        var template = await templates
            .GetActiveAsync(_options.PromptTemplateName, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No active prompt template named '{_options.PromptTemplateName}'. "
                + "Has the database been migrated?");

        var askedAt = timeProvider.GetUtcNow();

        var prompt = promptBuilder
            .WithTemplate(template)
            .WithHistory(conversation.Messages)
            .WithQuestion(command.Question)
            .WithBudget(_options.MaxPromptTokens, _options.MaxHistoryMessages)
            .Build();

        if (prompt.IsFailure)
        {
            return prompt.Error;
        }

        var completion = await chat.CompleteAsync(prompt.Value.Turns, cancellationToken).ConfigureAwait(false);
        if (completion.IsFailure)
        {
            // Nothing has been added to the aggregate, so there is nothing to undo.
            return completion.Error;
        }

        var answeredAt = timeProvider.GetUtcNow();

        var question = Message.FromUser(Guid.CreateVersion7(), conversation.Id, command.Question, askedAt);
        var answer = Message.FromAssistant(
            Guid.CreateVersion7(),
            conversation.Id,
            completion.Value.Content,
            completion.Value.Provider,
            completion.Value.Model,
            answeredAt,
            citations: null,
            tokenCount: completion.Value.OutputTokens);

        // The aggregate enforces the ordering (an answer must follow a question) and
        // throws on an archived conversation. This handler does not pre-check either:
        // that would be a second copy of the state machine (ADR-0011).
        conversation.AddUserMessage(question, askedAt);
        conversation.AddAssistantMessage(answer, answeredAt);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new AskQuestionResponse(
            conversation.Id,
            conversation.Title,
            question.ToResponse(),
            answer.ToResponse(),
            completion.Value.InputTokens?.Value,
            prompt.Value.HistoryMessagesIncluded,
            prompt.Value.HistoryMessagesOmitted,
            prompt.Value.TemplateVersion);
    }
}
