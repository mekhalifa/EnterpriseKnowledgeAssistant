using EKA.Domain.Common;

namespace EKA.Domain.Ingestion;

/// <summary>
/// How much a document should be trusted when sources disagree, from 0 to 100.
/// </summary>
/// <remarks>
/// <para>
/// Semantic similarity answers "which passage is about this question?" but not "which
/// passage should I believe?". When a superseded 2019 policy draft and the current signed
/// policy both match a question closely, similarity alone may rank the draft first, and
/// the assistant confidently cites the wrong one. Authority is the tie-breaker that makes
/// the ranking reflect institutional truth rather than only textual resemblance.
/// </para>
/// <para>
/// Safe as a struct: <c>default(AuthorityScore)</c> is 0, meaning "least trusted", which
/// is the correct failure direction for a value that was never set deliberately.
/// </para>
/// </remarks>
public readonly record struct AuthorityScore : IComparable<AuthorityScore>
{
    public const int MinValue = 0;
    public const int MaxValue = 100;

    public AuthorityScore(int value)
    {
        if (value is < MinValue or > MaxValue)
        {
            throw new DomainException(
                $"Authority score must be between {MinValue} and {MaxValue}; got {value}.");
        }

        Value = value;
    }

    public int Value { get; }

    /// <summary>Unreviewed content — drafts, scratch notes, user uploads.</summary>
    public static AuthorityScore Unverified => new(0);

    /// <summary>The default for ordinary ingested documents.</summary>
    public static AuthorityScore Standard => new(50);

    /// <summary>Approved, current, canonical — signed policies, published handbooks.</summary>
    public static AuthorityScore Authoritative => new(100);

    public int CompareTo(AuthorityScore other) => Value.CompareTo(other.Value);

    public static bool operator <(AuthorityScore left, AuthorityScore right) =>
        left.Value < right.Value;

    public static bool operator >(AuthorityScore left, AuthorityScore right) =>
        left.Value > right.Value;

    public static bool operator <=(AuthorityScore left, AuthorityScore right) =>
        left.Value <= right.Value;

    public static bool operator >=(AuthorityScore left, AuthorityScore right) =>
        left.Value >= right.Value;

    public override string ToString() => Value.ToString();
}
