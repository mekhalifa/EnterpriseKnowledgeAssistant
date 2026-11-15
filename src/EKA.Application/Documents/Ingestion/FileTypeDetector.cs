using System.Text.Unicode;
using EKA.Application.Abstractions.Ingestion;

namespace EKA.Application.Documents.Ingestion;

/// <summary>
/// Decides what a file is from its first bytes, and only then checks the claim.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never trust the declared type.</b> The <c>Content-Type</c> header and the filename
/// extension are both set by the client. A file named <c>policy.pdf</c> sent as
/// <c>application/pdf</c> can be anything. The bytes decide: <c>%PDF-</c> for PDF, a ZIP
/// local-file header for DOCX, and decodable UTF-8 with no NUL bytes for text. The extension
/// only chooses between formats the bytes cannot tell apart (Markdown vs plain text), and
/// must agree with the detected type, or the upload is refused.
/// </para>
/// <para>
/// <b>What this does not prove.</b> A ZIP header says "some ZIP", not "a valid Word
/// document", and <c>%PDF-</c> says nothing about the rest of the file. The extractor is
/// the second check: a file that passes here but cannot be parsed fails extraction as
/// <see cref="UnreadableDocumentException"/>.
/// </para>
/// </remarks>
public static class FileTypeDetector
{
    /// <summary>Bytes the caller must supply. Enough for every signature and a text sniff.</summary>
    public const int HeaderLength = 512;

    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    /// <param name="header">The first bytes of the file (up to <see cref="HeaderLength"/>).</param>
    /// <param name="fileName">The client's filename, used only to disambiguate text formats
    /// and to cross-check the bytes.</param>
    public static DocumentFileType Detect(ReadOnlySpan<byte> header, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (header.StartsWith(PdfSignature))
        {
            return extension == ".pdf" ? DocumentFileType.Pdf : DocumentFileType.Unknown;
        }

        if (header.StartsWith(ZipSignature))
        {
            // Every Office format, and every .zip, starts like this. Only the extension
            // narrows it to Word, and the extractor proves the rest.
            return extension == ".docx" ? DocumentFileType.Docx : DocumentFileType.Unknown;
        }

        if (LooksLikeText(header))
        {
            return extension switch
            {
                ".md" or ".markdown" => DocumentFileType.Markdown,
                ".txt" => DocumentFileType.PlainText,
                _ => DocumentFileType.Unknown,
            };
        }

        return DocumentFileType.Unknown;
    }

    /// <summary>The content type stored for a detected type, regardless of what the client claimed.</summary>
    public static string ContentTypeOf(DocumentFileType fileType) => fileType switch
    {
        DocumentFileType.Pdf => "application/pdf",
        DocumentFileType.Docx => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        DocumentFileType.PlainText => "text/plain",
        DocumentFileType.Markdown => "text/markdown",
        _ => throw new ArgumentOutOfRangeException(nameof(fileType), fileType, "No content type for an unknown file."),
    };

    /// <summary>
    /// The inverse, for the worker: the stored content type is the detector's own output, so
    /// mapping back is exact.
    /// </summary>
    public static DocumentFileType FromContentType(string contentType) => contentType switch
    {
        "application/pdf" => DocumentFileType.Pdf,
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => DocumentFileType.Docx,
        "text/plain" => DocumentFileType.PlainText,
        "text/markdown" => DocumentFileType.Markdown,
        _ => DocumentFileType.Unknown,
    };

    private static bool LooksLikeText(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty || header.Contains((byte)0))
        {
            return false;
        }

        // The header may cut a multi-byte character in half at its end, so allow up to three
        // trailing bytes to be an incomplete sequence. Anything invalid earlier is not UTF-8.
        for (var trim = 0; trim <= 3 && trim < header.Length; trim++)
        {
            if (Utf8.IsValid(header[..^trim]))
            {
                return true;
            }
        }

        return false;
    }
}
