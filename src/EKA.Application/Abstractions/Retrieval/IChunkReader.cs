using EKA.Domain.Access;
using EKA.Domain.Ingestion;

namespace EKA.Application.Abstractions.Retrieval;

/// <summary>
/// Reads the text of chunks a search found, from the source of truth, and only the ones the
/// caller may read right now.
/// </summary>
/// <remarks>
/// <para>
/// <b>The second check, and the one that decides.</b> The vector store filtered by tenant and
/// access keys already, but its copy of the keys can lag behind SQL Server (an access change
/// in flight, a sync that failed), and it knows nothing about ingestion status. This read
/// applies, inside one SQL query:
/// </para>
/// <list type="bullet">
/// <item><description>the caller's tenant;</description></item>
/// <item><description>the document's <i>current</i> access list against the caller's principals;</description></item>
/// <item><description><c>Status = Completed</c>, so a document mid-re-ingestion or failed is
/// never quoted, even if vectors for it still exist;</description></item>
/// <item><description>the exact <c>(DocumentId, ChunkIndex)</c> pairs, so an orphan vector
/// whose row is gone resolves to nothing.</description></item>
/// </list>
/// <para>
/// Written independently of the vector filter on purpose: a bug in one translation is not
/// automatically a bug in the other. This is defence in depth in its literal sense.
/// </para>
/// </remarks>
public interface IChunkReader
{
    /// <returns>The readable chunks among <paramref name="keys"/>, in no particular order.
    /// Keys that fail any check are simply absent.</returns>
    Task<IReadOnlyList<ChunkPassage>> ReadSearchableAsync(
        AccessScope caller,
        IReadOnlyCollection<ChunkKey> keys,
        CancellationToken cancellationToken = default);
}

/// <summary>Identifies one chunk across stores: the same pair the vector point id is derived from.</summary>
public readonly record struct ChunkKey(Guid DocumentId, ChunkIndex Index);

/// <summary>A chunk's text and where it came from: what a citation needs.</summary>
public sealed record ChunkPassage(
    Guid DocumentId,
    string DocumentTitle,
    ChunkIndex Index,
    string Content,
    PageRange? Pages,
    string? SectionPath)
{
    public ChunkKey Key => new(DocumentId, Index);
}
