namespace EKA.Application.Abstractions.Ingestion;

/// <summary>The kinds of file this system can extract text from, detected from content.</summary>
/// <remarks>Explicit values: logged and possibly persisted later.</remarks>
public enum DocumentFileType
{
    Unknown = 0,
    Pdf = 10,
    Docx = 20,
    PlainText = 30,
    Markdown = 40,
}

/// <summary>
/// One link in the extraction chain: it either claims a file type, or leaves it for the next.
/// </summary>
/// <remarks>
/// Chain of Responsibility (docs/concepts/design-patterns.md). The pipeline asks each
/// registered extractor in turn whether it handles the file, and the first that does
/// extracts it. Adding a format (HTML, PPTX) means registering one more extractor. Neither
/// the pipeline nor any other extractor changes. Implemented in <c>EKA.Infrastructure</c>,
/// because each one wraps a parsing library this layer must not see.
/// </remarks>
public interface ITextExtractor
{
    bool CanExtract(DocumentFileType fileType);

    /// <summary>Reads the whole document. Throws <see cref="UnreadableDocumentException"/>
    /// for content that is corrupt or not what its type claims.</summary>
    Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken cancellationToken = default);
}

/// <summary>Text pulled from one file, in reading order.</summary>
/// <param name="Sections">In order. A PDF yields one per page; DOCX and Markdown yield one
/// per heading-delimited section; plain text yields one.</param>
public sealed record ExtractedDocument(IReadOnlyList<ExtractedSection> Sections)
{
    public int CharacterCount => Sections.Sum(section => section.Text.Length);
}

/// <summary>A run of text and where it came from: what Step 8 chunks, and what Step 10 cites.</summary>
/// <param name="PageNumber">1-based page, for formats that have pages (PDF); otherwise null.</param>
/// <param name="SectionPath">Heading trail, e.g. <c>"Benefits / Leave"</c>, for formats with
/// headings; otherwise null.</param>
/// <param name="Text">Normalized text: no control characters, collapsed blank lines.</param>
public sealed record ExtractedSection(int? PageNumber, string? SectionPath, string Text);

/// <summary>
/// The file cannot be read as the type it was detected as: corrupt, encrypted, or lying
/// about its format.
/// </summary>
/// <remarks>
/// A <i>permanent</i> failure. The extraction handler records it on the document and does
/// not throw, so the job is not retried. Retrying a corrupt PDF five times produces the same
/// answer five times, more slowly. Any other exception (a storage outage, say) is treated as
/// transient and left to the job engine's retry.
/// </remarks>
public sealed class UnreadableDocumentException(string reason, Exception? innerException = null)
    : Exception(reason, innerException);
