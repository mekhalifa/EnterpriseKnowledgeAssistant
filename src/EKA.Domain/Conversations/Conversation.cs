using EKA.Domain.Common;

namespace EKA.Domain.Conversations;

/// <summary>
/// A question-and-answer thread between one user and the assistant. Aggregate root for its
/// messages.
/// </summary>
/// <remarks>
/// <para>
/// The aggregate boundary is what keeps message ordering and turn structure coherent:
/// messages are added through the root, so "an assistant answer always follows a question"
/// is enforceable in one place rather than hoped for at every call site.
/// </para>
/// </remarks>
public sealed class Conversation : EntityAudit<Guid>
{
    /// <summary>Longest auto-generated title before truncation.</summary>
    public const int MaxTitleLength = 120;

    private readonly List<Message> _messages = [];

    /// <summary>EF Core materialization only.</summary>
    private Conversation()
    {
    }

    public Conversation(Guid id, TenantId tenantId, Guid userId, DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        if (tenantId.IsEmpty)
        {
            throw new DomainException("A conversation must belong to a tenant.");
        }

        if (userId == Guid.Empty)
        {
            throw new DomainException("A conversation must belong to a user.");
        }

        TenantId = tenantId;
        UserId = userId;
    }

    public TenantId TenantId { get; private set; }

    /// <summary>
    /// The owner. Conversations are private to one person: this is read alongside
    /// <see cref="TenantId"/> on every load, and both are enforced in the query rather
    /// than checked after the fact.
    /// </summary>
    public Guid UserId { get; private set; }

    /// <summary>Null until the first question arrives, then derived from it.</summary>
    public string? Title { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool IsArchived => ArchivedAt is not null;

    public IReadOnlyCollection<Message> Messages => _messages.AsReadOnly();

    public Message? LastMessage => _messages.Count == 0 ? null : _messages[^1];

    public void AddUserMessage(Message message, DateTimeOffset at)
    {
        EnsureCanAccept(message, MessageRole.User);

        _messages.Add(message);

        // A thread titled "New conversation" in every row of the sidebar is useless. The
        // first question is the best short summary available at zero cost — no extra model
        // call, no latency.
        Title ??= Summarize(message.Content);

        MarkModified(at);
    }

    public void AddAssistantMessage(Message message, DateTimeOffset at)
    {
        EnsureCanAccept(message, MessageRole.Assistant);

        // An answer with no question is incoherent, and it is also how a prompt-injection
        // attempt would try to seed a thread with attacker-chosen "assistant" text that
        // later turns read as established context.
        if (LastMessage is null)
        {
            throw new DomainException(
                "A conversation cannot start with an assistant message.");
        }

        if (LastMessage.Role is MessageRole.Assistant)
        {
            throw new DomainException(
                "An assistant message must answer a user message, not another answer.");
        }

        _messages.Add(message);
        MarkModified(at);
    }

    public void Rename(string title, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainException("Conversation title cannot be empty.");
        }

        Title = Summarize(title);
        MarkModified(at);
    }

    public void Archive(DateTimeOffset at)
    {
        if (IsArchived)
        {
            throw new InvalidStateTransitionException(
                nameof(Conversation), "Archived", "Archived");
        }

        ArchivedAt = at;
        MarkModified(at);
    }

    public void Restore(DateTimeOffset at)
    {
        if (!IsArchived)
        {
            throw new InvalidStateTransitionException(
                nameof(Conversation), "Active", "Active");
        }

        ArchivedAt = null;
        MarkModified(at);
    }

    private void EnsureCanAccept(Message message, MessageRole expectedRole)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (IsArchived)
        {
            throw new DomainException(
                $"Conversation {Id} is archived and cannot accept new messages.");
        }

        if (message.ConversationId != Id)
        {
            throw new DomainException(
                $"Message belongs to conversation {message.ConversationId}, not {Id}.");
        }

        if (message.Role != expectedRole)
        {
            throw new DomainException(
                $"Expected a {expectedRole} message but got {message.Role}.");
        }
    }

    private static string Summarize(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length <= MaxTitleLength
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, MaxTitleLength - 1).TrimEnd(), "…");
    }

    public override string ToString() => Title ?? $"Conversation {Id}";
}
