using EKA.Domain.Common;

namespace EKA.Application.Abstractions.Ingestion;

/// <summary>
/// Durable storage for uploaded files and the artifacts derived from them.
/// </summary>
/// <remarks>
/// <para>
/// Keys are opaque, path-like strings produced by <c>StorageKeys</c>. They are never built
/// from raw user input, and every adapter must still refuse a key that escapes its root,
/// because a port cannot assume all its callers are this code.
/// </para>
/// <para>
/// <b>Writes are atomic per key:</b> after <see cref="SaveAsync"/> returns, the key holds
/// the whole content. If it throws, the key holds what it held before. A reader never sees
/// half a file. A worker that crashes mid-write must not leave a truncated PDF for the next
/// attempt to extract garbage from.
/// </para>
/// </remarks>
public interface IFileStorage
{
    /// <summary>Writes <paramref name="content"/> to <paramref name="key"/>, replacing any existing content.</summary>
    Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Opens the content for reading. The caller disposes the stream.</summary>
    /// <exception cref="FileNotFoundException">Nothing is stored under the key.</exception>
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Removes the key. Not an error if it does not exist: deletion is idempotent.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Hands a document to the ingestion worker.
/// </summary>
/// <remarks>
/// <para>
/// <b>Best effort, by contract.</b> The document row and the queued job are two writes to
/// two places, and no transaction spans both. Rather than pretend otherwise, the hand-off
/// may fail and say so (<c>false</c>). The document row is the source of truth: a document
/// left <c>Pending</c> is picked up by the stalled-document sweep
/// (<c>RecoverStalledDocumentsHandler</c>), so a lost hand-off delays ingestion and never
/// loses it. ADR-0020.
/// </para>
/// <para>
/// The corollary: the worker must tolerate receiving a document twice, once from the upload
/// and once from the sweep. It does, because the aggregate decides whether extraction is
/// still owed (<c>Document.AwaitsExtraction</c>).
/// </para>
/// </remarks>
public interface IIngestionQueue
{
    /// <summary>
    /// Queues the whole pipeline for one document: extraction, chunking, embedding. Named
    /// <c>EnqueueExtractionAsync</c> until Step 8, when one job began running every stage.
    /// </summary>
    /// <returns><c>true</c> if the job was durably queued; <c>false</c> if the hand-off failed
    /// and the sweep will have to recover it.</returns>
    Task<bool> EnqueueIngestionAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A write was rejected by a unique index: someone else got there first.
/// </summary>
/// <remarks>
/// <para>
/// One of the two exceptions this layer recognizes from persistence, and the only one it
/// is expected to <i>handle</i>. A check-then-insert can always lose a race, and the index
/// is what makes losing safe. Translated from the provider's error in
/// <c>EkaDbContext.SaveChangesAsync</c>, so no handler ever references EF Core or SQL Server
/// error numbers.
/// </para>
/// <para>
/// After it is thrown the unit of work still tracks the rejected entity. A handler that
/// catches it should read what won the race and return. Saving again through the same unit
/// of work would retry the same failing insert.
/// </para>
/// </remarks>
public sealed class DuplicateKeyException(string indexName, Exception innerException)
    : Exception($"A unique index rejected the write: {indexName}.", innerException)
{
    /// <summary>The violated index, as named in the database (e.g. <c>UX_Documents_TenantId_ContentHash</c>).</summary>
    public string IndexName { get; } = indexName;
}
