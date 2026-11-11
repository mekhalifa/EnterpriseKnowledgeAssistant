namespace EKA.Domain.Common;

/// <summary>
/// A number of model tokens — the unit that pricing, context windows, and prompt budgets
/// are all measured in.
/// </summary>
/// <remarks>
/// Wrapping the <see cref="int"/> prevents the mistake this project is most exposed to:
/// passing a character count, a word count, or a chunk size where a token count belongs.
/// They are all ints, they are all plausible magnitudes, and the resulting bug is a prompt
/// that silently overflows the context window in production.
/// <para>
/// Safe as a struct: <c>default(TokenCount)</c> is zero, which is a legitimate value.
/// </para>
/// </remarks>
public readonly record struct TokenCount : IComparable<TokenCount>
{
    public TokenCount(int value)
    {
        if (value < 0)
        {
            throw new DomainException($"Token count cannot be negative; got {value}.");
        }

        Value = value;
    }

    public int Value { get; }

    public static TokenCount Zero => new(0);

    public static TokenCount operator +(TokenCount left, TokenCount right) =>
        new(left.Value + right.Value);

    public static bool operator <(TokenCount left, TokenCount right) => left.Value < right.Value;

    public static bool operator >(TokenCount left, TokenCount right) => left.Value > right.Value;

    public static bool operator <=(TokenCount left, TokenCount right) => left.Value <= right.Value;

    public static bool operator >=(TokenCount left, TokenCount right) => left.Value >= right.Value;

    public int CompareTo(TokenCount other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString();
}
