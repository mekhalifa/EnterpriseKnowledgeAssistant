using System.Text;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Documents.Ingestion;
using EKA.Application.Tests.Fakes;
using Shouldly;

namespace EKA.Application.Tests.Documents;

public sealed class FileTypeDetectorTests
{
    private static readonly byte[] Pdf = "%PDF-1.7\n"u8.ToArray();
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00];
    private static readonly byte[] Text = "Leave policy\nEmployees receive 25 days."u8.ToArray();

    [Theory]
    [InlineData("policy.pdf", DocumentFileType.Pdf)]
    [InlineData("POLICY.PDF", DocumentFileType.Pdf)]
    [InlineData("policy.docx", DocumentFileType.Unknown)]
    [InlineData("policy.txt", DocumentFileType.Unknown)]
    public void A_pdf_is_recognized_by_its_signature_and_must_be_named_as_one(string name, DocumentFileType expected)
    {
        FileTypeDetector.Detect(Pdf, name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("policy.docx", DocumentFileType.Docx)]
    [InlineData("policy.pdf", DocumentFileType.Unknown)]
    [InlineData("archive.zip", DocumentFileType.Unknown)]
    public void A_zip_is_only_accepted_as_a_word_document(string name, DocumentFileType expected)
    {
        FileTypeDetector.Detect(Zip, name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("notes.txt", DocumentFileType.PlainText)]
    [InlineData("notes.md", DocumentFileType.Markdown)]
    [InlineData("notes.markdown", DocumentFileType.Markdown)]
    [InlineData("notes.pdf", DocumentFileType.Unknown)]
    [InlineData("notes", DocumentFileType.Unknown)]
    public void Text_is_text_whatever_it_claims_and_the_extension_only_picks_the_flavour(string name, DocumentFileType expected)
    {
        FileTypeDetector.Detect(Text, name).ShouldBe(expected);
    }

    [Fact]
    public void A_binary_file_named_txt_is_not_text()
    {
        // An executable renamed to .txt: the NUL bytes give it away.
        FileTypeDetector.Detect([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00], "readme.txt").ShouldBe(DocumentFileType.Unknown);
    }

    [Fact]
    public void A_multibyte_character_cut_off_by_the_header_window_is_still_text()
    {
        // "é" is two bytes in UTF-8. A header that ends between them is a truncation, not
        // a sign of binary content.
        var bytes = Encoding.UTF8.GetBytes("Résumé");
        FileTypeDetector.Detect(bytes.AsSpan(0, bytes.Length - 1), "cv.txt").ShouldBe(DocumentFileType.PlainText);
    }

    [Fact]
    public void Content_types_round_trip_through_storage()
    {
        foreach (var type in new[] { DocumentFileType.Pdf, DocumentFileType.Docx, DocumentFileType.PlainText, DocumentFileType.Markdown })
        {
            FileTypeDetector.FromContentType(FileTypeDetector.ContentTypeOf(type)).ShouldBe(type);
        }
    }
}

public sealed class TextExtractionPipelineTests
{
    [Fact]
    public async Task Every_format_is_normalized_the_same_way()
    {
        // CRLF to LF, NUL and form-feed removed, trailing spaces trimmed, blank runs
        // collapsed. Exercised through the pipeline, which is the only way text reaches Step 8.
        var raw = "Title\r\n\r\n\r\n\r\nBody\u0000 text\u000C here   \r\nend\t";
        var pipeline = new TextExtractionPipeline([FakeExtractor.Returning(DocumentFileType.PlainText, raw)]);

        var result = await pipeline.ExtractAsync(DocumentFileType.PlainText, Stream.Null);

        result.Sections.Single().Text.ShouldBe("Title\n\nBody text here\nend");
    }

    [Fact]
    public async Task The_first_extractor_that_claims_the_type_wins()
    {
        var pdf = FakeExtractor.Returning(DocumentFileType.Pdf, "from pdf");
        var text = FakeExtractor.Returning(DocumentFileType.PlainText, "from text");
        var pipeline = new TextExtractionPipeline([pdf, text]);

        var result = await pipeline.ExtractAsync(DocumentFileType.PlainText, Stream.Null);

        result.Sections.Single().Text.ShouldBe("from text");
        pdf.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task An_unhandled_type_is_unreadable_not_a_crash()
    {
        var pipeline = new TextExtractionPipeline([FakeExtractor.Returning(DocumentFileType.Pdf, "x")]);

        await Should.ThrowAsync<UnreadableDocumentException>(
            () => pipeline.ExtractAsync(DocumentFileType.Docx, Stream.Null));
    }

    [Fact]
    public async Task A_document_with_no_text_at_all_is_unreadable()
    {
        // The scanned-PDF case: pages exist, text does not. "Completed" would be a lie.
        var pipeline = new TextExtractionPipeline([FakeExtractor.Returning(DocumentFileType.Pdf, "  ", "\u0000\n\n", "")]);

        var exception = await Should.ThrowAsync<UnreadableDocumentException>(
            () => pipeline.ExtractAsync(DocumentFileType.Pdf, Stream.Null));

        exception.Message.ShouldContain("OCR");
    }

    [Fact]
    public async Task Empty_sections_are_dropped_but_the_rest_keep_their_page_numbers()
    {
        var pipeline = new TextExtractionPipeline([FakeExtractor.Returning(DocumentFileType.Pdf, "page one", " ", "page three")]);

        var result = await pipeline.ExtractAsync(DocumentFileType.Pdf, Stream.Null);

        result.Sections.Select(s => s.PageNumber).ShouldBe([1, 3]);
    }
}
