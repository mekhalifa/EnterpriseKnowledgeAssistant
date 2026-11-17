using EKA.Application.Abstractions;
using EKA.Application.Abstractions.AI;
using EKA.Application.Common;
using EKA.Application.Identity;
using EKA.Application.Retrieval;
using EKA.Application.Usage;
using EKA.Contracts.Conversations;
using EKA.Domain.Conversations;
using EKA.Domain.Prompting;
using Microsoft.Extensions.Options;

namespace EKA.Application.Conversations.AskQuestion;

/// <summary>
/// Answers a question from the documents the caller may read, with citations, in the
/// context of a conversation, and records the exchange.
/// </summary>
/// <remarks>
/// <para>
/// <b>The flow since Step 10</b> (retrieval-augmented generation):
/// </para>
/// <list type="number">
/// <item><description><b>Retrieve</b> with the caller's <c>AccessScope</c>, through the same
/// <see cref="ChunkRetriever"/> as <c>POST /api/search</c>. A document the caller cannot search
/// can never reach their prompt.</description></item>
/// <item><description><b>Build</b> the prompt: numbered sources within their own budget, then
/// history.</description></item>
/// <item><description><b>No sources, no model.</b> If nothing relevant was found (or nothing
/// fitted), the system answers with <see cref="ChatOptions.NoEvidenceAnswer"/>. That costs
/// nothing and cannot be fabricated, because no model was asked (ADR-0029).</description></item>
/// <item><description><b>Otherwise ask the model,</b> which is instructed to answer only from
/// the sources, cite them as [n], and say so when they do not cover the question.</description></item>
/// <item><description><b>Extract citations</b> from the markers the model wrote. Only sources
/// it was given count, renumbered in order of appearance. An answer citing nothing is stored
/// as ungrounded, which is what a model's own refusal looks like.</description></item>
/// </list>
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
    ChunkRetriever retriever,
    TokenBudget budget,
    IChatCompletionService chat,
    PromptBuilder promptBuilder,
    IUnitOfWork unitOfWork,
    IOptions<ChatOptions> options,
    TimeProvider timeProvider)
{
    private readonly ChatOptions _options = options.Value;

    public Task<Result<AskQuestionResponse>> HandleAsync(
        AskQuestionCommand command,
        CancellationToken cancellationToken = default) =>
        AskAsync(command, onAnswerDelta: null, cancellationToken);

    /// <summary>
    /// The same use case, with the model's text handed to <paramref name="onAnswerDelta"/> as it is
    /// written (Step 13, ADR-0033).
    /// </summary>
    /// <remarks>
    /// Everything else is identical, deliberately: the same checks, the same retrieval with the
    /// caller's scope, the same atomic turn. The deltas are raw model output, with the model's own
    /// <c>[n]</c> numbering; the returned response carries the renumbered answer and its
    /// citations, and is the only thing persisted. If the provider fails after some deltas, the
    /// result is that failure and nothing is saved. A no-evidence refusal produces no deltas at all,
    /// because no model is called.
    /// </remarks>
    public Task<Result<AskQuestionResponse>> StreamAsync(
        AskQuestionCommand command,
        Func<string, CancellationToken, ValueTask> onAnswerDelta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onAnswerDelta);

        return AskAsync(command, onAnswerDelta, cancellationToken);
    }

    private async Task<Result<AskQuestionResponse>> AskAsync(
        AskQuestionCommand command,
        Func<string, CancellationToken, ValueTask>? onAnswerDelta,
        CancellationToken cancellationToken)
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

        // Same reasoning: the migration seeds a grounded v2. An active template without
        // {{context}} would answer from the model's training data, silently.
        if (!template.SupportsGrounding)
        {
            throw new InvalidOperationException(
                $"The active prompt template '{template.Name}' v{template.Version} has no "
                + $"{PromptTemplate.ContextPlaceholder} placeholder and cannot produce grounded answers.");
        }

        // Step 12: a spent monthly budget stops the turn before any provider call. Checked after
        // the cheap checks (conversation, template), so a wrong id is still a 404, not a 429.
        var available = await budget.EnsureAvailableAsync(command.Caller.TenantId, cancellationToken).ConfigureAwait(false);
        if (available.IsFailure)
        {
            return available.Error;
        }

        var askedAt = timeProvider.GetUtcNow();

        var retrieved = await retriever
            .RetrieveAsync(command.Caller.AccessScope, command.Question, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (retrieved.IsFailure)
        {
            // The question was not embedded or not searched: a provider or search failure, not
            // "no evidence". Nothing is persisted, as with a failed model call.
            return retrieved.Error;
        }

        var prompt = promptBuilder
            .WithTemplate(template)
            .WithHistory(conversation.Messages)
            .WithQuestion(command.Question)
            .WithSources(retrieved.Value, _options.MaxContextTokens)
            .WithBudget(_options.MaxPromptTokens, _options.MaxHistoryMessages)
            .Build();

        if (prompt.IsFailure)
        {
            return prompt.Error;
        }

        var question = Message.FromUser(Guid.CreateVersion7(), conversation.Id, command.Question, askedAt);
        Message answer;
        string grounding;
        int? promptTokens = null;

        if (prompt.Value.Sources.Count == 0)
        {
            answer = Message.NoEvidenceRefusal(Guid.CreateVersion7(), conversation.Id, _options.NoEvidenceAnswer, timeProvider.GetUtcNow());
            grounding = GroundingNoEvidence;
        }
        else
        {
            var completion = onAnswerDelta is null
                ? await chat.CompleteAsync(prompt.Value.Turns, cancellationToken).ConfigureAwait(false)
                : await chat.StreamAsync(prompt.Value.Turns, onAnswerDelta, cancellationToken).ConfigureAwait(false);
            if (completion.IsFailure)
            {
                // Nothing has been added to the aggregate, so there is nothing to undo.
                return completion.Error;
            }

            await budget.RecordChatAsync(command.Caller.TenantId, completion.Value, prompt.Value.EstimatedTokens.Value, cancellationToken)
                .ConfigureAwait(false);

            var extracted = CitationMarkers.Extract(completion.Value.Content, prompt.Value.Sources);

            answer = Message.FromAssistant(
                Guid.CreateVersion7(),
                conversation.Id,
                // An answer that was nothing but invented markers would extract to nothing.
                // Keep the model's words rather than fail the turn; it cites nothing either way.
                string.IsNullOrWhiteSpace(extracted.Text) ? completion.Value.Content : extracted.Text,
                completion.Value.Provider,
                completion.Value.Model,
                timeProvider.GetUtcNow(),
                citations: extracted.Cited.Select(ToCitation),
                tokenCount: completion.Value.OutputTokens,
                promptTemplateVersion: prompt.Value.TemplateVersion);

            grounding = extracted.Cited.Count > 0 ? GroundingCited : GroundingUncited;
            promptTokens = completion.Value.InputTokens?.Value;
        }

        var answeredAt = answer.CreatedAt;

        // The aggregate enforces the ordering (an answer must follow a question) and
        // throws on an archived conversation. This handler does not pre-check either:
        // that would be a second copy of the state machine (ADR-0011).
        conversation.AddUserMessage(question, askedAt);
        conversation.AddAssistantMessage(answer, answeredAt);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var modelCalled = grounding is not GroundingNoEvidence;

        return new AskQuestionResponse(
            conversation.Id,
            conversation.Title,
            question.ToResponse(),
            answer.ToResponse(),
            promptTokens,
            modelCalled ? prompt.Value.HistoryMessagesIncluded : 0,
            modelCalled ? prompt.Value.HistoryMessagesOmitted : 0,
            prompt.Value.TemplateVersion,
            grounding,
            SourcesRetrieved: retrieved.Value.Count,
            SourcesProvided: prompt.Value.Sources.Count);
    }

    internal const string GroundingCited = "cited";
    internal const string GroundingUncited = "uncited";
    internal const string GroundingNoEvidence = "no_evidence";

    private static Citation ToCitation(PromptSource source) =>
        new(
            source.Chunk.Passage.DocumentId,
            source.Chunk.Passage.DocumentTitle,
            source.Chunk.Passage.Index,
            source.Chunk.Score,
            source.Number,
            source.Chunk.Passage.Pages,
            source.Chunk.Passage.SectionPath);
}
