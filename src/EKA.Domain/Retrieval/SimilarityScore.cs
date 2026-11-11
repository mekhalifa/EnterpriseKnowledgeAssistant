using EKA.Domain.Common;

namespace EKA.Domain.Retrieval;

/// <summary>
/// How closely a chunk matched a query, normalized to 0..1 where 1 is identical meaning.
/// </summary>
/// <remarks>
/// <para>
/// Normalizing at the domain boundary matters because vector stores do not agree on what
/// "score" means: cosine similarity runs -1..1, cosine <i>distance</i> runs 0..2 and is
/// better when <i>lower</i>, and dot product is unbounded. Letting a raw provider number
/// reach the application layer means a score threshold configured for one metric silently
/// inverts under another — a filter that was keeping the best results starts keeping the
/// worst, with no error anywhere.
/// </para>
/// <para>
/// Safe as a struct: <c>default</c> is 0.0, meaning "no similarity", which is the correct
/// direction to fail in.
/// </para>
/// </remarks>
public readonly record struct SimilarityScore : IComparable<SimilarityScore>
{
    public SimilarityScore(double value)
    {
        if (double.IsNaN(value))
        {
            throw new DomainException("Similarity score cannot be NaN.");
        }

        if (value is < 0d or > 1d)
        {
            throw new DomainException(
                $"Similarity score must be between 0 and 1; got {value}. "
                + "Provider-specific metrics must be normalized before reaching the domain.");
        }

        Value = value;
    }

    public double Value { get; }

    public static SimilarityScore None => new(0d);

    public static SimilarityScore Exact => new(1d);

    /// <summary>Converts a cosine similarity (-1..1) to this normalized scale.</summary>
    public static SimilarityScore FromCosineSimilarity(double cosine)
    {
        if (double.IsNaN(cosine))
        {
            throw new DomainException("Cosine similarity cannot be NaN.");
        }

        var clamped = Math.Clamp(cosine, -1d, 1d);
        return new SimilarityScore((clamped + 1d) / 2d);
    }

    public int CompareTo(SimilarityScore other) => Value.CompareTo(other.Value);

    public static bool operator <(SimilarityScore left, SimilarityScore right) =>
        left.Value < right.Value;

    public static bool operator >(SimilarityScore left, SimilarityScore right) =>
        left.Value > right.Value;

    public static bool operator <=(SimilarityScore left, SimilarityScore right) =>
        left.Value <= right.Value;

    public static bool operator >=(SimilarityScore left, SimilarityScore right) =>
        left.Value >= right.Value;

    public override string ToString() => Value.ToString("0.###");
}
