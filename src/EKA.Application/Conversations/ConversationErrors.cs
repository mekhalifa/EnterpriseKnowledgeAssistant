using EKA.Application.Common;

namespace EKA.Application.Conversations;

internal static class ConversationErrors
{
    /// <remarks>
    /// Also returned for a conversation owned by another user or tenant: confirming that
    /// it exists would be the leak (see <see cref="ErrorType.NotFound"/>).
    /// </remarks>
    public static Error NotFound(Guid conversationId) => Error.NotFound(
        "conversation.not_found",
        $"No conversation with id {conversationId} was found.");

    /// <summary>
    /// The question plus the system instruction exceeds the whole prompt budget, before
    /// any history. A 400 here, from a stateful rule (it depends on the active template),
    /// rather than at the edge: the edge only knows the raw character limit.
    /// </summary>
    public static Error QuestionTooLong(int maxPromptTokens) => Error.Validation(
        "conversation.question_too_long",
        $"The question is too long to answer within the {maxPromptTokens}-token prompt budget.");
}
