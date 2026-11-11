using EKA.Domain.Common;

namespace EKA.Domain.Ingestion;

/// <summary>
/// A chunk's zero-based position within its document. Ordering matters: citations point at
/// an index, and adjacent chunks are what gets expanded when a retrieved passage needs
/// surrounding context.
/// </summary>
/// <remarks>
/// <para>
/// The reason to wrap an <see cref="int"/> is the signature it protects:
/// <c>new DocumentChunk(documentId, chunkIndex, content)</c> has an int and a string that
/// a caller can transpose with a page number, a token count, or an offset — all ints, all
/// plausible, and the compiler cannot tell. With this type the mistake does not compile.
/// </para>
/// <para>
/// Safe as a struct: <c>default(ChunkIndex)</c> is zero, which is the first valid index.
/// </para>
/// </remarks>
public readonly record struct ChunkIndex : IComparable<ChunkIndex>
{
    public ChunkIndex(int value)
    {
        if (value < 0)
        {
            throw new DomainException($"Chunk index cannot be negative; got {value}.");
        }

        Value = value;
    }

    public int Value { get; }

    public static ChunkIndex First => new(0);

    public ChunkIndex Next() => new(Value + 1);

    public int CompareTo(ChunkIndex other) => Value.CompareTo(other.Value);

    public static bool operator <(ChunkIndex left, ChunkIndex right) => left.Value < right.Value;

    public static bool operator >(ChunkIndex left, ChunkIndex right) => left.Value > right.Value;

    public static bool operator <=(ChunkIndex left, ChunkIndex right) => left.Value <= right.Value;

    public static bool operator >=(ChunkIndex left, ChunkIndex right) => left.Value >= right.Value;

    public override string ToString() => Value.ToString();
}
