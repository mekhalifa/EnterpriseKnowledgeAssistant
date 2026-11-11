namespace EKA.Domain.Providers;

/// <summary>
/// Which service backs a configured provider.
/// </summary>
/// <remarks>
/// Kept deliberately small. Adding a provider means adding a member here and an adapter in
/// <c>EKA.AI</c> — nothing in <c>EKA.Application</c> changes, because use cases talk to
/// <c>IChatClient</c> and never to a concrete service. That is the payoff of the Adapter
/// pattern described in docs/concepts/design-patterns.md.
/// </remarks>
public enum AIProviderKind
{
    /// <summary>api.openai.com, authenticated with an API key.</summary>
    OpenAI = 10,

    /// <summary>An Azure OpenAI deployment — tenant-scoped endpoint, supports managed
    /// identity and private networking, which is usually what "enterprise" requires.</summary>
    AzureOpenAI = 20,
}
