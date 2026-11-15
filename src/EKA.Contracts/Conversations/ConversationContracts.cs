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
/// <param name="Model">The model that wrote an answer; null for a question.</param>
public sealed record MessageResponse(
    Guid Id,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    int? TokenCount,
    string? Model);

public sealed record AskQuestionRequest(string Question);

/// <summary>The exchange just persisted, plus how the prompt was built.</summary>
/// <param name="PromptTokens">Input tokens as the provider counted them, if it reported.</param>
/// <param name="HistoryMessagesIncluded">Prior messages the model was shown.</param>
/// <param name="HistoryMessagesOmitted">Prior messages trimmed to fit the budget. Non-zero
/// means the answer was written without the whole conversation in view.</param>
/// <param name="PromptTemplateVersion">Which version of the prompt template was used.</param>
public sealed record AskQuestionResponse(
    Guid ConversationId,
    string? Title,
    MessageResponse Question,
    MessageResponse Answer,
    int? PromptTokens,
    int HistoryMessagesIncluded,
    int HistoryMessagesOmitted,
    int PromptTemplateVersion);
