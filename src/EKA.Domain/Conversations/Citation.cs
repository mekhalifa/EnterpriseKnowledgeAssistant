using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using EKA.Domain.Retrieval;

namespace EKA.Domain.Conversations;

/// <summary>
/// A pointer from an answer back to the exact chunk that supports it.
/// </summary>
/// <remarks>
/// <para>
/// Citations are what separate a knowledge assistant from a chatbot that sounds confident.
/// An uncited answer cannot be checked, so it cannot be trusted, so it cannot be used for
/// anything that matters.
/// </para>
/// <para>
/// <b>Why the document title is copied in rather than looked up.</b> A citation records
/// what was cited <i>at the time the answer was given</i>. If the document is later
/// renamed, retitled, or deleted, resolving the title live would silently rewrite history
/// — an old answer would start claiming it cited a document that did not exist under that
/// name. Snapshotting costs a duplicated string and buys an auditable record.
/// <see cref="DocumentId"/> is still kept for navigation when the document does still
/// exist.
/// </para>
/// </remarks>
public sealed record Citation
{
    public Citation(
        Guid documentId,
        string documentTitle,
        ChunkIndex chunkIndex,
        SimilarityScore score,
        PageRange? pageRange = null,
        string? sectionPath = null)
    {
        if (documentId == Guid.Empty)
        {
            throw new DomainException("A citation must reference a document.");
        }

        if (string.IsNullOrWhiteSpace(documentTitle))
        {
            throw new DomainException("A citation must record the document title it cited.");
        }

        DocumentId = documentId;
        DocumentTitle = documentTitle.Trim();
        ChunkIndex = chunkIndex;
        Score = score;
        PageRange = pageRange;
        SectionPath = sectionPath?.Trim();
    }

    public Guid DocumentId { get; }

    /// <summary>The title as it stood when the answer was produced. Deliberately a copy.</summary>
    public string DocumentTitle { get; }

    public ChunkIndex ChunkIndex { get; }

    public SimilarityScore Score { get; }

    public PageRange? PageRange { get; }

    public string? SectionPath { get; }

    public override string ToString() =>
        PageRange is null ? DocumentTitle : $"{DocumentTitle}, {PageRange}";
}
