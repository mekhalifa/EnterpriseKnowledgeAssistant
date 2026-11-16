using Microsoft.Extensions.Options;

namespace EKA.Application.Retrieval;

/// <summary>The <c>Retrieval</c> configuration section: how many chunks, and how close.</summary>
/// <remarks>
/// Configuration, not per-request values, because they are tuning decisions Step 11 measures
/// and changes, and a client should not be able to undo them. A request may ask for <i>fewer</i>
/// chunks than <see cref="MaxTopK"/>; that is the only knob it gets.
/// </remarks>
public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    /// <summary>Chunks returned when a request does not say. Five ≈ 2,500 tokens of context.</summary>
    public int DefaultTopK { get; set; } = 5;

    /// <summary>The most a request may ask for. Larger requests are clamped, not refused.</summary>
    public int MaxTopK { get; set; } = 20;

    /// <summary>
    /// The lowest normalized score (0..1, see <c>SimilarityScore</c>) worth returning. 0.6 is
    /// cosine similarity 0.2, a deliberately low starting floor for <c>text-embedding-3-small</c>,
    /// whose related passages typically score 0.3 to 0.6 in raw cosine. It removes noise, not
    /// borderline evidence. Step 11 sets it from data.
    /// </summary>
    public double MinimumScore { get; set; } = 0.6;

    /// <summary>
    /// How many candidates to ask the vector store for, per chunk wanted. Some candidates are
    /// dropped by the SQL re-check (a document mid-re-ingestion, an orphan vector, access that
    /// just changed), and asking for exactly K would then return fewer than K.
    /// </summary>
    public int CandidateMultiplier { get; set; } = 2;

    /// <summary>Longest question accepted, in characters. A question, not a document.</summary>
    public int MaxQueryLength { get; set; } = 2000;
}

public sealed class RetrievalOptionsValidator : IValidateOptions<RetrievalOptions>
{
    public ValidateOptionsResult Validate(string? name, RetrievalOptions options)
    {
        var failures = new List<string>();

        if (options.MaxTopK is < 1 or > 100)
        {
            failures.Add($"{RetrievalOptions.SectionName}:MaxTopK must be between 1 and 100; got {options.MaxTopK}.");
        }

        if (options.DefaultTopK < 1 || options.DefaultTopK > options.MaxTopK)
        {
            failures.Add($"{RetrievalOptions.SectionName}:DefaultTopK must be between 1 and MaxTopK ({options.MaxTopK}); got {options.DefaultTopK}.");
        }

        if (double.IsNaN(options.MinimumScore) || options.MinimumScore is < 0 or > 1)
        {
            failures.Add($"{RetrievalOptions.SectionName}:MinimumScore is a normalized score and must be between 0 and 1; got {options.MinimumScore}.");
        }

        if (options.CandidateMultiplier is < 1 or > 10)
        {
            failures.Add($"{RetrievalOptions.SectionName}:CandidateMultiplier must be between 1 and 10; got {options.CandidateMultiplier}.");
        }

        if (options.MaxQueryLength is < 1 or > 32_000)
        {
            failures.Add($"{RetrievalOptions.SectionName}:MaxQueryLength must be between 1 and 32000; got {options.MaxQueryLength}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
