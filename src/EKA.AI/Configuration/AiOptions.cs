using EKA.Domain.Providers;

namespace EKA.AI.Configuration;

/// <summary>
/// The <c>Ai</c> configuration section: which provider, which models, and how hard to try.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where each value lives.</b> Everything except <see cref="ApiKey"/> is ordinary,
/// committable configuration in <c>appsettings.json</c>. <see cref="ApiKey"/> comes from
/// User Secrets locally (<c>dotnet user-secrets set "Ai:ApiKey" …</c>), the environment
/// variable <c>Ai__ApiKey</c> in containers, and Key Vault in production — never a committed
/// file.
/// </para>
/// <para>
/// <b>No defaults for the things that matter.</b> Model names, endpoint and dimensions have
/// no fallback in code. A default here would be a second, invisible source of truth, and the
/// one most likely to be wrong — an embedding dimension silently defaulted to 1536 while the
/// configured model produces 3072 is precisely the garbage-vector failure the validator
/// exists to stop.
/// </para>
/// </remarks>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public AIProviderKind Kind { get; set; }

    /// <summary>Base address, for example <c>https://openrouter.ai/api/v1</c>.</summary>
    /// <remarks>Required for every kind except <see cref="AIProviderKind.OpenAI"/>, which
    /// has a fixed public host.</remarks>
    public Uri? Endpoint { get; set; }

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model slug as the provider names it — <c>openai/gpt-4o-mini</c> on
    /// OpenRouter, <c>gpt-4o-mini</c> on OpenAI direct.</summary>
    public string ChatModel { get; set; } = string.Empty;

    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>
    /// Vector length requested from, and demanded of, the embedding model. Fixes the shape of
    /// the Qdrant collection (Step 8); changing it means re-indexing every document.
    /// </summary>
    public int EmbeddingDimensions { get; set; }

    /// <summary>
    /// Sampling temperature for chat completions (Step 11). Null leaves the provider's default
    /// (1.0 for OpenAI models).
    /// </summary>
    /// <remarks>
    /// The shipped configuration sets 0. Grounded answers should vary as little as possible:
    /// the same question over the same sources should get the same answer. And evaluation
    /// should measure the configuration that answers users, not a calmer one set up for
    /// testing. Zero reduces variance; it does not remove it, because providers are not
    /// bit-for-bit deterministic, which is why the evaluation gate has a tolerance.
    /// </remarks>
    public float? Temperature { get; set; }

    /// <summary>
    /// The most tokens one chat answer may produce (Step 11). Null leaves the model's maximum.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A cost control, not a style choice.</b> Without it, every request asks for the model's
    /// maximum output (16,384 tokens for gpt-4o). OpenRouter reserves credit for that maximum
    /// before answering, so a nearly spent budget refuses requests it could easily afford.
    /// That is how this was found: the evaluation's judge calls were refused with 402. It also
    /// puts a ceiling on a runaway answer.
    /// </para>
    /// <para>
    /// The shipped configuration sets 1,000: grounded answers here run to a few sentences
    /// (under 120 tokens in the evaluation), and a cut-off answer is visible and fixable, while
    /// an unbounded one is a bill.
    /// </para>
    /// </remarks>
    public int? MaxOutputTokens { get; set; }

    public AiResilienceOptions Resilience { get; set; } = new();
}

/// <summary>
/// Timeouts and retries for calls to the provider. Unlike the model settings these do have
/// defaults: they are tuning, not identity, and a wrong value degrades latency rather than
/// corrupting data.
/// </summary>
public sealed class AiResilienceOptions
{
    /// <summary>
    /// One HTTP attempt. Generous because a chat completion streams nothing back until the
    /// model has finished — 10 seconds, the HTTP resilience default, would time out ordinary
    /// long answers and then retry them, paying for the same tokens twice.
    /// </summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The whole call, including every retry and backoff delay.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(180);

    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Base of the exponential, jittered backoff. Zero in tests.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long the circuit stays open before a probe request is let through.</summary>
    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}
