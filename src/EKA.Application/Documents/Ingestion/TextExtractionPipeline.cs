using System.Text;
using System.Text.RegularExpressions;
using EKA.Application.Abstractions.Ingestion;

namespace EKA.Application.Documents.Ingestion;

/// <summary>
/// Picks the extractor for a file (Chain of Responsibility), runs it, and normalizes what
/// comes back, so every format reaches Step 8 in the same shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three stages, fixed order:</b> select (first extractor that claims the type), extract
/// (format-specific), normalize (format-independent). Normalization lives here rather than
/// in each extractor, so "what clean text means" has one definition. Otherwise three
/// extractors would drift into three definitions, and the chunker would see a different
/// kind of whitespace depending on the source format.
/// </para>
/// <para>
/// <b>Empty is a failure.</b> A PDF that is a scanned image yields pages with no text.
/// Treating that as success would produce a "completed" document that can never be found.
/// Rejecting it gives the user a reason they can act on.
/// </para>
/// </remarks>
public sealed partial class TextExtractionPipeline(IEnumerable<ITextExtractor> extractors)
{
    private readonly IReadOnlyList<ITextExtractor> _extractors = [.. extractors];

    public async Task<ExtractedDocument> ExtractAsync(
        DocumentFileType fileType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var extractor = _extractors.FirstOrDefault(candidate => candidate.CanExtract(fileType))
            ?? throw new UnreadableDocumentException($"No extractor handles {fileType} files.");

        var raw = await extractor.ExtractAsync(content, cancellationToken).ConfigureAwait(false);

        var sections = raw.Sections
            .Select(section => section with { Text = Normalize(section.Text) })
            .Where(section => section.Text.Length > 0)
            .ToList();

        if (sections.Count == 0)
        {
            throw new UnreadableDocumentException(
                "The file contains no extractable text. If it is a scanned document, it needs OCR, which is not supported yet.");
        }

        return new ExtractedDocument(sections);
    }

    /// <summary>
    /// Line endings to <c>\n</c>, control characters removed (tab and newline kept),
    /// trailing spaces trimmed per line, runs of blank lines collapsed to one.
    /// </summary>
    /// <remarks>
    /// Control characters are not cosmetic. PDFs routinely contain NUL and form-feed bytes,
    /// SQL Server and JSON tolerate them inconsistently, and tokenizers count them as tokens
    /// that mean nothing.
    /// </remarks>
    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            if (character is '\n' or '\t' || !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        var trimmedLines = TrailingSpace().Replace(builder.ToString(), "\n");
        return BlankLines().Replace(trimmedLines, "\n\n").Trim();
    }

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
