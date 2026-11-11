namespace EKA.Domain.Ingestion;

/// <summary>
/// Where a document sits in the ingestion pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Persisted after every stage, which is what lets a retry <b>resume</b> instead of
/// restarting. Re-extracting a 300-page PDF and re-paying for thousands of embeddings
/// because the last stage failed is the difference between a cheap retry and an expensive
/// one.
/// </para>
/// <para>
/// Explicit numeric values because this enum is stored in the database. Relying on
/// declaration order means inserting a stage later silently renumbers every existing row.
/// Gaps are left between values so a future stage (OCR, translation) can slot in without
/// a data migration.
/// </para>
/// <para>
/// The legal transitions live in <see cref="Document"/>, not here — an enum can hold the
/// vocabulary but cannot enforce a sequence.
/// </para>
/// </remarks>
public enum IngestionStatus
{
    /// <summary>Accepted and stored; no processing has started.</summary>
    Pending = 10,

    /// <summary>Pulling text out of the source file.</summary>
    Extracting = 20,

    /// <summary>Splitting extracted text into chunks.</summary>
    Chunking = 30,

    /// <summary>Generating embeddings and writing them to the vector store.</summary>
    Embedding = 40,

    /// <summary>Searchable. The only status from which a document may be retrieved.</summary>
    Completed = 50,

    /// <summary>A stage failed. Carries a reason and may be reset for a retry.</summary>
    Failed = 90,
}
