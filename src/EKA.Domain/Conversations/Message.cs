using EKA.Domain.Common;

namespace EKA.Domain.Conversations;

/// <summary>
/// One turn in a conversation. Part of the <see cref="Conversation"/> aggregate.
/// </summary>
/// <remarks>
/// <para>
/// Constructed through <see cref="FromUser"/> and <see cref="FromAssistant"/> rather than a
/// public constructor, because the rules differ by role and a single constructor taking
/// every optional parameter could not enforce them: only an assistant message may carry
/// citations, a provider, a model, or a token count. A user message with a model name
/// attached is nonsense that a general constructor would happily accept.
/// </para>
/// <para>
/// Messages are immutable once created. An edited answer is a new message, not a mutated
/// one — otherwise the citations recorded against it describe text that no longer exists.
/// </para>
/// </remarks>
public sealed class Message : EntityAudit<Guid>
{
    private readonly List<Citation> _citations = [];

    /// <summary>EF Core materialization only.</summary>
    private Message()
    {
    }

    private Message(
        Guid id,
        Guid conversationId,
        MessageRole role,
        string content,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        if (conversationId == Guid.Empty)
        {
            throw new DomainException("A message must belong to a conversation.");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new DomainException($"A {role} message cannot be empty.");
        }

        ConversationId = conversationId;
        Role = role;
        Content = content.Trim();
    }

    public Guid ConversationId { get; private set; }

    public MessageRole Role { get; private set; }

    public string Content { get; private set; } = string.Empty;

    /// <summary>Tokens this message consumed. Null for user messages, which are not billed
    /// separately from the request that carried them.</summary>
    public TokenCount? TokenCount { get; private set; }

    /// <summary>Which configured provider produced an assistant message.</summary>
    public string? ProviderName { get; private set; }

    /// <summary>
    /// The exact model that produced an assistant message. Recorded per message because
    /// models change underneath you — when answer quality shifts, "which model wrote this
    /// one?" is the first question, and it is unanswerable after the fact otherwise.
    /// </summary>
    public string? ModelName { get; private set; }

    public IReadOnlyCollection<Citation> Citations => _citations.AsReadOnly();

    /// <summary>
    /// True when an assistant message carries no sources. Expected and correct for a
    /// refusal ("the documents do not cover this"); a warning sign for a confident answer,
    /// which is why Step 11 measures the rate.
    /// </summary>
    public bool IsUngrounded => Role is MessageRole.Assistant && _citations.Count == 0;

    public static Message FromUser(
        Guid id,
        Guid conversationId,
        string content,
        DateTimeOffset createdAt) =>
        new(id, conversationId, MessageRole.User, content, createdAt);

    public static Message FromAssistant(
        Guid id,
        Guid conversationId,
        string content,
        string providerName,
        string modelName,
        DateTimeOffset createdAt,
        IEnumerable<Citation>? citations = null,
        TokenCount? tokenCount = null)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new DomainException("An assistant message must record its provider.");
        }

        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new DomainException("An assistant message must record its model.");
        }

        var message = new Message(id, conversationId, MessageRole.Assistant, content, createdAt)
        {
            ProviderName = providerName.Trim(),
            ModelName = modelName.Trim(),
            TokenCount = tokenCount,
        };

        if (citations is not null)
        {
            // Deduplicate: retrieval regularly returns several chunks from the same
            // document, and the reader does not want the same source listed five times.
            var distinct = citations
                .DistinctBy(c => (c.DocumentId, c.ChunkIndex.Value))
                .OrderByDescending(c => c.Score);

            message._citations.AddRange(distinct);
        }

        return message;
    }

    public override string ToString() => $"{Role}: {Content[..Math.Min(60, Content.Length)]}";
}
