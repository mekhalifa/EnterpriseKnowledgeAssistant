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

    /// <summary>
    /// The prompt template version that produced an assistant message (Step 11). Null for user
    /// messages, for no-evidence refusals (no prompt was sent), and for answers older than
    /// Step 11.
    /// </summary>
    /// <remarks>
    /// The model name says who wrote an answer. This says what they were told. When evaluation
    /// shows a change in quality, both are needed to tell a model change from a prompt change.
    /// </remarks>
    public int? PromptTemplateVersion { get; private set; }

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
        TokenCount? tokenCount = null,
        int? promptTemplateVersion = null)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new DomainException("An assistant message must record its provider.");
        }

        if (promptTemplateVersion is < 1)
        {
            throw new DomainException($"Prompt template versions start at 1; got {promptTemplateVersion}.");
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
            PromptTemplateVersion = promptTemplateVersion,
        };

        if (citations is not null)
        {
            // Deduplicate: the same chunk cited twice is one source, not two.
            var distinct = citations
                .DistinctBy(c => (c.DocumentId, c.ChunkIndex.Value))
                .OrderBy(c => c.Number)
                .ToList();

            // Ordered by marker, not score (Step 10): the reader follows [1], [2] through the
            // text, and the list beneath it should read in the same order. Two different
            // sources sharing a marker would make "[2]" point at two places.
            var clash = distinct.GroupBy(c => c.Number).FirstOrDefault(group => group.Count() > 1);
            if (clash is not null)
            {
                throw new DomainException($"Two different sources share citation marker [{clash.Key}].");
            }

            message._citations.AddRange(distinct);
        }

        return message;
    }

    /// <summary>The provider name recorded on an answer the system wrote without a model.</summary>
    public const string SystemProvider = "eka";

    /// <summary>The "model" recorded on a no-evidence refusal: none was asked.</summary>
    public const string NoEvidenceModel = "none:no-evidence";

    /// <summary>
    /// The answer given when retrieval found nothing the caller may read that is relevant
    /// enough. Written by the system, without a model call (Step 10).
    /// </summary>
    /// <remarks>
    /// Still an assistant message, so the conversation keeps its question-answer rhythm and
    /// the next prompt's history reads naturally. It records <see cref="SystemProvider"/> and
    /// <see cref="NoEvidenceModel"/> instead of pretending a model wrote it: "which model said
    /// this?" has the true answer, none.
    /// </remarks>
    public static Message NoEvidenceRefusal(Guid id, Guid conversationId, string content, DateTimeOffset createdAt) =>
        FromAssistant(id, conversationId, content, SystemProvider, NoEvidenceModel, createdAt);

    /// <summary>True for an answer the system gave because no relevant document was found.</summary>
    public bool IsNoEvidenceRefusal =>
        Role is MessageRole.Assistant
        && ProviderName == SystemProvider
        && ModelName == NoEvidenceModel;

    public override string ToString() => $"{Role}: {Content[..Math.Min(60, Content.Length)]}";
}
