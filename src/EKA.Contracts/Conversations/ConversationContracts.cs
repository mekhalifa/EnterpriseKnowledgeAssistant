namespace EKA.Contracts.Conversations;

/// <summary>A conversation with its messages in order, oldest first.</summary>
public sealed record ConversationResponse(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ModifiedAt,
    IReadOnlyList<MessageResponse> Messages);

/// <summary>A row in the caller's conversation list.</summary>
public sealed record ConversationSummaryResponse(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);

/// <param name="Role">"User" or "Assistant". System turns are never stored and never returned.</param>
/// <param name="TokenCount">Output tokens for an answer; null for a question.</param>
/// <param name="Model">The model that wrote an answer; null for a question. <c>none:no-evidence</c>
/// when the system answered without one because nothing relevant was found.</param>
/// <param name="Citations">The sources behind an answer, in marker order: citation
/// <c>Number</c> 2 is what <c>[2]</c> in <paramref name="Content"/> refers to. Empty for a
/// question, and for an answer that cites nothing.</param>
public sealed record MessageResponse(
    Guid Id,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    int? TokenCount,
    string? Model,
    IReadOnlyList<CitationResponse> Citations);

/// <summary>One source behind an answer: enough to open the document at the right place.</summary>
/// <param name="Number">The marker in the answer text, <c>[Number]</c>.</param>
/// <param name="DocumentId">The document, for navigation. It may since have been deleted.</param>
/// <param name="DocumentTitle">The title when the answer was given. A snapshot, not a lookup.</param>
/// <param name="ChunkIndex">The passage's position in the document, from 0.</param>
/// <param name="PageStart">First page, for paged formats.</param>
/// <param name="PageEnd">Last page, for paged formats.</param>
/// <param name="SectionPath">Heading trail, if known.</param>
/// <param name="Score">Retrieval similarity, 0 to 1.</param>
public sealed record CitationResponse(
    int Number,
    Guid DocumentId,
    string DocumentTitle,
    int ChunkIndex,
    int? PageStart,
    int? PageEnd,
    string? SectionPath,
    double Score);

public sealed record AskQuestionRequest(string Question);

/// <summary>
/// The server-sent event names of <c>POST /api/conversations/{id}/messages/stream</c> (Step 13,
/// ADR-0033). A stream is zero or more <see cref="Delta"/> events, then exactly one
/// <see cref="Completed"/> or <see cref="Error"/>, then the response ends.
/// </summary>
public static class AnswerStreamEvents
{
    /// <summary>A piece of the model's raw answer: <see cref="AnswerDeltaEvent"/>. For display only;
    /// its <c>[n]</c> markers are the model's numbering, not the final citation numbers.</summary>
    public const string Delta = "delta";

    /// <summary>The persisted exchange: <see cref="AskQuestionResponse"/>. Replaces everything the
    /// deltas showed.</summary>
    public const string Completed = "completed";

    /// <summary>A failure after the stream had started: an RFC 9457 problem document. Nothing was
    /// persisted. A failure <i>before</i> the first event is an ordinary HTTP error status instead.</summary>
    public const string Error = "error";
}

/// <summary>One piece of answer text as the model wrote it.</summary>
public sealed record AnswerDeltaEvent(string Text);

/// <summary>The exchange just persisted, plus how the answer was produced.</summary>
/// <param name="PromptTokens">Input tokens as the provider counted them, if it reported.
/// Null when no model was called.</param>
/// <param name="HistoryMessagesIncluded">Prior messages the model was shown.</param>
/// <param name="HistoryMessagesOmitted">Prior messages trimmed to fit the budget. Non-zero
/// means the answer was written without the whole conversation in view.</param>
/// <param name="PromptTemplateVersion">Which version of the prompt template was used.</param>
/// <param name="Grounding">
/// <c>cited</c>: the answer cites at least one source. <c>uncited</c>: the model was given
/// sources and cited none, usually because it said they do not cover the question.
/// <c>no_evidence</c>: retrieval found nothing relevant the caller may read, and the system
/// answered without calling the model.
/// </param>
/// <param name="SourcesRetrieved">Chunks the retriever returned.</param>
/// <param name="SourcesProvided">Of those, how many fitted the context budget and were shown
/// to the model.</param>
public sealed record AskQuestionResponse(
    Guid ConversationId,
    string? Title,
    MessageResponse Question,
    MessageResponse Answer,
    int? PromptTokens,
    int HistoryMessagesIncluded,
    int HistoryMessagesOmitted,
    int PromptTemplateVersion,
    string Grounding,
    int SourcesRetrieved,
    int SourcesProvided);
