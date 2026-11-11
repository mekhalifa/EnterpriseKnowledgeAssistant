using EKA.Domain.Common;

namespace EKA.Domain.Providers;

/// <summary>
/// A configured model backend: which service, which models, and where to find its
/// credential — never the credential itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is deliberately no ApiKey property, and there never will be.</b> A secret in a
/// database column is a secret in every backup, every replica, every developer's restored
/// snapshot, and every log line that dumps an entity. It is also the first thing an
/// attacker reads after any SQL injection. This entity stores
/// <see cref="SecretReference"/> — the <i>name</i> of a secret — and the actual value is
/// resolved at call time from User Secrets locally, environment variables in containers,
/// or Key Vault in production. The README states this as a Definition of Done item; the
/// model enforces it by having nowhere to put a key.
/// </para>
/// <para>
/// <b>Why embedding dimensions live here.</b> The dimension count is fixed by the
/// embedding model and determines the shape of the Qdrant collection. Vectors of different
/// dimensions are not comparable, so changing this value invalidates every stored vector
/// and forces a full re-index. Recording it per provider makes that dependency visible
/// instead of a number buried in configuration.
/// </para>
/// </remarks>
public sealed class AIProvider : EntityAudit<Guid>
{
    /// <summary>EF Core materialization only.</summary>
    private AIProvider()
    {
    }

    public AIProvider(
        Guid id,
        string name,
        AIProviderKind kind,
        string chatModel,
        string embeddingModel,
        int embeddingDimensions,
        string secretReference,
        DateTimeOffset createdAt,
        string? endpoint = null)
        : base(id, createdAt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Provider name is required.");
        }

        if (string.IsNullOrWhiteSpace(chatModel))
        {
            throw new DomainException("A provider must declare a chat model.");
        }

        if (string.IsNullOrWhiteSpace(embeddingModel))
        {
            throw new DomainException("A provider must declare an embedding model.");
        }

        if (embeddingDimensions <= 0)
        {
            throw new DomainException(
                $"Embedding dimensions must be positive; got {embeddingDimensions}.");
        }

        if (string.IsNullOrWhiteSpace(secretReference))
        {
            throw new DomainException(
                "A provider must name the secret holding its credential. "
                + "Store the reference, never the credential.");
        }

        // An Azure OpenAI deployment is reached at a tenant-specific endpoint; there is no
        // shared default to fall back on, so omitting it is a configuration error that
        // should fail here rather than as a confusing 404 on the first chat request.
        if (kind is AIProviderKind.AzureOpenAI && string.IsNullOrWhiteSpace(endpoint))
        {
            throw new DomainException("Azure OpenAI requires an endpoint.");
        }

        Name = name.Trim();
        Kind = kind;
        ChatModel = chatModel.Trim();
        EmbeddingModel = embeddingModel.Trim();
        EmbeddingDimensions = embeddingDimensions;
        SecretReference = secretReference.Trim();
        Endpoint = endpoint?.Trim();
        IsEnabled = true;
    }

    public string Name { get; private set; } = string.Empty;

    public AIProviderKind Kind { get; private set; }

    public string ChatModel { get; private set; } = string.Empty;

    public string EmbeddingModel { get; private set; } = string.Empty;

    /// <summary>
    /// Vector length produced by <see cref="EmbeddingModel"/>. Changing it means
    /// re-indexing every document — see this type's remarks.
    /// </summary>
    public int EmbeddingDimensions { get; private set; }

    /// <summary>
    /// The <i>name</i> of the secret holding this provider's credential — for example
    /// <c>"Eka:Providers:AzureOpenAI:ApiKey"</c>. Never the credential.
    /// </summary>
    public string SecretReference { get; private set; } = string.Empty;

    /// <summary>Required for Azure OpenAI; null for providers with a fixed public host.</summary>
    public string? Endpoint { get; private set; }

    public bool IsEnabled { get; private set; }

    public void Disable(DateTimeOffset at)
    {
        IsEnabled = false;
        MarkModified(at);
    }

    public void Enable(DateTimeOffset at)
    {
        IsEnabled = true;
        MarkModified(at);
    }

    /// <summary>
    /// Points the provider at a different chat model. Safe at any time — unlike the
    /// embedding model, changing which model writes answers does not invalidate anything
    /// already stored.
    /// </summary>
    public void UseChatModel(string chatModel, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(chatModel))
        {
            throw new DomainException("Chat model is required.");
        }

        ChatModel = chatModel.Trim();
        MarkModified(at);
    }

    public override string ToString() => $"{Name} ({Kind})";
}
