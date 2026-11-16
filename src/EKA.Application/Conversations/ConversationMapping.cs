using EKA.Contracts.Conversations;
using EKA.Domain.Conversations;

namespace EKA.Application.Conversations;

internal static class ConversationMapping
{
    public static ConversationResponse ToResponse(this Conversation conversation) =>
        new(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAt,
            conversation.ModifiedAt,
            [.. conversation.Messages.Select(ToResponse)]);

    public static ConversationSummaryResponse ToSummary(this Conversation conversation) =>
        new(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAt,
            conversation.ModifiedAt ?? conversation.CreatedAt);

    public static MessageResponse ToResponse(this Message message) =>
        new(
            message.Id,
            message.Role.ToString(),
            message.Content,
            message.CreatedAt,
            message.TokenCount?.Value,
            message.ModelName,
            [.. message.Citations.OrderBy(citation => citation.Number).Select(ToResponse)]);

    public static CitationResponse ToResponse(this Citation citation) =>
        new(
            citation.Number,
            citation.DocumentId,
            citation.DocumentTitle,
            citation.ChunkIndex.Value,
            citation.PageRange?.Start,
            citation.PageRange?.End,
            citation.SectionPath,
            Math.Round(citation.Score.Value, 4));
}
