using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using EKA.Application.Abstractions.Ingestion;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace EKA.Infrastructure.Extraction;

/// <summary>
/// What counts as "this file is broken" versus "something went wrong while reading it".
/// </summary>
/// <remarks>
/// Parsers throw a zoo of exception types for malformed input, and none is worth retrying:
/// the same bytes fail the same way. They all become <see cref="UnreadableDocumentException"/>,
/// which is permanent. I/O errors and cancellation are excluded, because a disk or network
/// hiccup may well succeed on retry (ADR-0022).
/// </remarks>
internal static class ParserFailure
{
    public static bool IsPermanent(Exception exception) => exception switch
    {
        OperationCanceledException or OutOfMemoryException => false,
        // A truncated file surfaces as end-of-stream, an IOException subtype that is in fact
        // a property of the bytes, so it must be matched before IOException below.
        EndOfStreamException or InvalidDataException => true,
        IOException => false,
        _ => true,
    };
}

/// <summary>PDF via PdfPig: one section per page, text in content order.</summary>
/// <remarks>
/// <see cref="ContentOrderTextExtractor"/> rather than <c>page.Text</c>: the latter
/// concatenates glyphs in drawing order, which for many generators means words run together
/// and columns interleave. Content order is still not layout analysis. Multi-column pages
/// and tables can come out in a surprising order, which is the gap Azure Document
/// Intelligence would close (ADR-0022).
/// </remarks>
internal sealed class PdfTextExtractor : ITextExtractor
{
    public bool CanExtract(DocumentFileType fileType) => fileType is DocumentFileType.Pdf;

    public Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken cancellationToken = default)
    {
        try
        {
            using var pdf = PdfDocument.Open(content);

            var sections = new List<ExtractedSection>(pdf.NumberOfPages);
            foreach (var page in pdf.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                sections.Add(new ExtractedSection(page.Number, SectionPath: null, ContentOrderTextExtractor.GetText(page)));
            }

            return Task.FromResult(new ExtractedDocument(sections));
        }
        catch (Exception exception) when (ParserFailure.IsPermanent(exception))
        {
            throw new UnreadableDocumentException($"The PDF could not be read: {exception.Message}", exception);
        }
    }
}

/// <summary>DOCX via the Open XML SDK: one section per heading, carrying the heading trail.</summary>
/// <remarks>
/// A heading is a paragraph whose style is <c>Title</c> or <c>Heading1</c>…<c>Heading9</c>,
/// the built-in styles Word applies when an author uses its heading buttons. A document
/// that fakes headings with bold body text yields one big section. That is the honest result
/// of formatting that carries no structure.
/// </remarks>
internal sealed class DocxTextExtractor : ITextExtractor
{
    public bool CanExtract(DocumentFileType fileType) => fileType is DocumentFileType.Docx;

    public Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken cancellationToken = default)
    {
        try
        {
            using var word = WordprocessingDocument.Open(content, isEditable: false);
            var body = word.MainDocumentPart?.Document?.Body
                ?? throw new UnreadableDocumentException("The Word document has no body.");

            var builder = new SectionBuilder();
            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var text = paragraph.InnerText;
                var level = HeadingLevel(paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value);

                if (level is { } headingLevel && !string.IsNullOrWhiteSpace(text))
                {
                    builder.StartSection(headingLevel, text.Trim());
                }
                else
                {
                    builder.AppendLine(text);
                }
            }

            return Task.FromResult(new ExtractedDocument(builder.Build()));
        }
        catch (Exception exception) when (exception is not UnreadableDocumentException && ParserFailure.IsPermanent(exception))
        {
            throw new UnreadableDocumentException($"The Word document could not be read: {exception.Message}", exception);
        }
    }

    private static int? HeadingLevel(string? styleId) => styleId switch
    {
        null => null,
        "Title" => 1,
        _ when styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(styleId.AsSpan("Heading".Length), out var level)
               && level is >= 1 and <= 9 => level,
        _ => null,
    };
}

/// <summary>Plain text and Markdown. Markdown is split at ATX headings (<c>#</c> … <c>######</c>).</summary>
/// <remarks>
/// Decoded as UTF-8, honouring a byte-order mark. Invalid byte sequences become U+FFFD
/// rather than failing the document: one mis-encoded character in a long policy should not
/// make the rest unsearchable.
/// </remarks>
internal sealed partial class PlainTextExtractor : ITextExtractor
{
    public bool CanExtract(DocumentFileType fileType) =>
        fileType is DocumentFileType.PlainText or DocumentFileType.Markdown;

    public async Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var builder = new SectionBuilder();
        foreach (var line in text.Split('\n'))
        {
            var heading = AtxHeading().Match(line);
            if (heading.Success)
            {
                builder.StartSection(heading.Groups["hashes"].Length, heading.Groups["title"].Value.Trim());
            }
            else
            {
                builder.AppendLine(line.TrimEnd('\r'));
            }
        }

        return new ExtractedDocument(builder.Build());
    }

    // Plain text has no headings by definition, but a .txt containing "# Title" lines is
    // Markdown in all but name, and splitting it costs nothing and helps citations.
    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<title>.+?)\s*#*\s*$")]
    private static partial Regex AtxHeading();
}

/// <summary>
/// Accumulates text under a heading trail. A heading at level <i>n</i> closes the current
/// section and replaces every heading at level <i>n</i> or deeper.
/// </summary>
internal sealed class SectionBuilder
{
    private readonly List<ExtractedSection> _sections = [];
    private readonly SortedDictionary<int, string> _trail = [];
    private readonly StringBuilder _current = new();

    public void StartSection(int level, string heading)
    {
        Flush();

        foreach (var deeper in _trail.Keys.Where(key => key >= level).ToList())
        {
            _trail.Remove(deeper);
        }

        _trail[level] = heading;

        // The heading text is part of the section's content too: "Annual leave" is often the
        // very phrase a question uses.
        _current.AppendLine(heading);
    }

    public void AppendLine(string line) => _current.AppendLine(line);

    public IReadOnlyList<ExtractedSection> Build()
    {
        Flush();
        return _sections;
    }

    private void Flush()
    {
        if (_current.Length == 0)
        {
            return;
        }

        var path = _trail.Count == 0 ? null : string.Join(" / ", _trail.Values);
        _sections.Add(new ExtractedSection(PageNumber: null, path, _current.ToString()));
        _current.Clear();
    }
}
