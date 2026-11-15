using EKA.Domain.Providers;
using Microsoft.Extensions.Options;

namespace EKA.AI.Configuration;

/// <summary>
/// Rejects an <see cref="AiOptions"/> the adapter cannot work with. Runs at startup through
/// <c>ValidateOnStart()</c>, so a bad configuration stops the process instead of failing the
/// first user's first question.
/// </summary>
/// <remarks>
/// <para>
/// <b>A hand-written validator rather than data annotations.</b> Most of these rules are
/// conditional (an endpoint is required <i>unless</i> the kind is OpenAI) or cross-field
/// (total timeout must exceed attempt timeout), which attributes express badly. It also
/// avoids a package for six <c>if</c> statements.
/// </para>
/// <para>
/// <b>What this cannot catch.</b> Whether the configured model really produces
/// <see cref="AiOptions.EmbeddingDimensions"/> components is only knowable by calling it, so
/// the embedding service checks every vector it receives as well. Startup validation catches
/// the typo; the runtime check catches the lie.
/// </para>
/// </remarks>
internal sealed class AiOptionsValidator : IValidateOptions<AiOptions>
{
    /// <summary>
    /// The largest vector any current embedding model emits is 3072; Qdrant accepts far more.
    /// The ceiling exists to catch a pasted token count or byte size, not to limit models.
    /// </summary>
    private const int MaxEmbeddingDimensions = 8192;

    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var section = AiOptions.SectionName;

        switch (options.Kind)
        {
            case AIProviderKind.OpenAI:
            case AIProviderKind.OpenAICompatible:
                break;

            case AIProviderKind.AzureOpenAI:
                // Recognized by the domain, not yet adapted. Failing loudly here beats
                // routing an Azure configuration through the OpenAI adapter and getting a
                // 404 from a URL shape Azure does not serve. See ADR-0015.
                failures.Add($"{section}:Kind 'AzureOpenAI' is not supported yet; see ADR-0015.");
                break;

            default:
                failures.Add(
                    $"{section}:Kind is missing or unknown ('{options.Kind}'). "
                    + $"Expected one of: {string.Join(", ", Enum.GetNames<AIProviderKind>())}.");
                break;
        }

        if (options.Kind is not AIProviderKind.OpenAI && options.Endpoint is null)
        {
            failures.Add($"{section}:Endpoint is required for provider kind '{options.Kind}'.");
        }

        if (options.Endpoint is { } endpoint
            && (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps)
            && !IsLoopback(endpoint))
        {
            // An http:// endpoint sends the API key in clear text. Loopback is exempt so a
            // local Ollama on http://localhost:11434 works.
            failures.Add($"{section}:Endpoint must be an absolute https URI (got '{endpoint}').");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add(
                $"{section}:ApiKey is not configured. Set it with "
                + "'dotnet user-secrets --project src/EKA.Api set \"Ai:ApiKey\" <key>' locally, "
                + "or the Ai__ApiKey environment variable in a container.");
        }

        if (string.IsNullOrWhiteSpace(options.ChatModel))
        {
            failures.Add($"{section}:ChatModel is required.");
        }

        if (string.IsNullOrWhiteSpace(options.EmbeddingModel))
        {
            failures.Add($"{section}:EmbeddingModel is required.");
        }

        if (options.EmbeddingDimensions is <= 0 or > MaxEmbeddingDimensions)
        {
            failures.Add(
                $"{section}:EmbeddingDimensions must be between 1 and {MaxEmbeddingDimensions} "
                + $"(got {options.EmbeddingDimensions}). It fixes the vector collection shape; "
                + "a wrong value produces vectors that cannot be compared with stored ones.");
        }

        ValidateResilience(options.Resilience, $"{section}:Resilience", failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateResilience(
        AiResilienceOptions resilience,
        string section,
        List<string> failures)
    {
        // The HTTP resilience package validates its own options at startup too, but phrases
        // failures in its own terms ("Total request timeout resilience strategy…") with no
        // hint of which configuration key to change. These checks name the key. Where both
        // fire, the operator gets both messages — redundant, but never ambiguous.
        if (resilience.AttemptTimeout < TimeSpan.FromMilliseconds(10))
        {
            failures.Add($"{section}:AttemptTimeout must be at least 10 ms.");
        }

        if (resilience.TotalTimeout <= resilience.AttemptTimeout)
        {
            failures.Add($"{section}:TotalTimeout must exceed AttemptTimeout, or no retry can ever run.");
        }

        if (resilience.MaxRetryAttempts is < 0 or > 10)
        {
            failures.Add($"{section}:MaxRetryAttempts must be between 0 and 10.");
        }

        if (resilience.RetryBaseDelay < TimeSpan.Zero)
        {
            failures.Add($"{section}:RetryBaseDelay cannot be negative.");
        }

        if (resilience.CircuitBreakDuration < TimeSpan.FromMilliseconds(500))
        {
            failures.Add($"{section}:CircuitBreakDuration must be at least 500 ms.");
        }
    }

    private static bool IsLoopback(Uri endpoint) =>
        endpoint.IsAbsoluteUri && endpoint.IsLoopback;
}
