using EKA.Application.Abstractions;
using EKA.Application.Abstractions.AI;
using EKA.Application.Common;
using EKA.Domain.Common;
using EKA.Domain.Conversations;
using EKA.Domain.Identity;
using EKA.Domain.Prompting;

namespace EKA.Application.Tests.Fakes;

/// <summary>In-memory users, scoped by tenant exactly like the SQL repository, because
/// that scoping is the rule Step 6 fixed (see <see cref="FakeDocumentRepository"/> for why
/// fakes mirror the filter).</summary>
internal sealed class FakeUserRepository : IUserRepository
{
    private readonly List<User> _users = [];

    public IReadOnlyList<User> All => _users;

    public Task<User?> GetByIdAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_users.FirstOrDefault(user => user.TenantId == tenantId && user.Id == userId));

    public Task<User?> FindByExternalIdAsync(TenantId tenantId, string externalId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_users.FirstOrDefault(user => user.TenantId == tenantId && user.ExternalId == externalId));

    public void Add(User user) => _users.Add(user);
}

/// <summary>Scoped by tenant AND user, like the SQL repository.</summary>
internal sealed class FakeConversationRepository : IConversationRepository
{
    private readonly List<Conversation> _conversations = [];

    public IReadOnlyList<Conversation> All => _conversations;

    public Task<Conversation?> GetByIdAsync(TenantId tenantId, Guid userId, Guid conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Find(tenantId, userId, conversationId));

    public Task<Conversation?> GetWithMessagesAsync(TenantId tenantId, Guid userId, Guid conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Find(tenantId, userId, conversationId));

    public Task<IReadOnlyList<Conversation>> GetActiveForUserAsync(TenantId tenantId, Guid userId, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Conversation>>(
            [.. _conversations.Where(c => c.TenantId == tenantId && c.UserId == userId && !c.IsArchived).Take(limit)]);

    public void Add(Conversation conversation) => _conversations.Add(conversation);

    public void Remove(Conversation conversation) => _conversations.Remove(conversation);

    private Conversation? Find(TenantId tenantId, Guid userId, Guid conversationId) =>
        _conversations.FirstOrDefault(c => c.TenantId == tenantId && c.UserId == userId && c.Id == conversationId);
}

internal sealed class FakePromptTemplateRepository : IPromptTemplateRepository
{
    private readonly List<PromptTemplate> _templates = [];

    public Task<PromptTemplate?> GetActiveAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_templates.FirstOrDefault(t => t.Name == name && t.IsActive));

    public Task<PromptTemplate?> GetVersionAsync(string name, int version, CancellationToken cancellationToken = default) =>
        Task.FromResult(_templates.FirstOrDefault(t => t.Name == name && t.Version == version));

    public Task<IReadOnlyList<PromptTemplate>> GetVersionsAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PromptTemplate>>([.. _templates.Where(t => t.Name == name)]);

    public void Add(PromptTemplate template) => _templates.Add(template);
}

/// <summary>Records every prompt it is sent; answers with a fixed reply or a fixed error.</summary>
internal sealed class FakeChatCompletionService : IChatCompletionService
{
    public List<IReadOnlyList<ChatTurn>> Prompts { get; } = [];

    public Error? FailWith { get; set; }

    public string Answer { get; set; } = "An answer.";

    public Task<Result<ChatCompletion>> CompleteAsync(IReadOnlyList<ChatTurn> conversation, CancellationToken cancellationToken = default)
    {
        Prompts.Add(conversation);

        Result<ChatCompletion> result = FailWith is { } error
            ? error
            : new ChatCompletion(Answer, "fake.provider", "fake-model", new TokenCount(50), new TokenCount(7));

        return Task.FromResult(result);
    }

    /// <summary>When set with <see cref="FailWith"/>, a stream emits this many deltas before failing:
    /// a provider dying half-way through an answer.</summary>
    public int DeltasBeforeFailure { get; set; }

    /// <summary>Streams <see cref="Answer"/> word by word (spaces kept), or fails as configured.</summary>
    public async Task<Result<ChatCompletion>> StreamAsync(
        IReadOnlyList<ChatTurn> conversation,
        Func<string, CancellationToken, ValueTask> onDelta,
        CancellationToken cancellationToken = default)
    {
        Prompts.Add(conversation);

        var pieces = System.Text.RegularExpressions.Regex.Split(Answer, @"(?<= )");
        var limit = FailWith is null ? pieces.Length : Math.Min(DeltasBeforeFailure, pieces.Length);

        foreach (var piece in pieces.Take(limit))
        {
            await onDelta(piece, cancellationToken);
        }

        return FailWith is { } error
            ? error
            : new ChatCompletion(Answer, "fake.provider", "fake-model", new TokenCount(50), new TokenCount(7));
    }
}
