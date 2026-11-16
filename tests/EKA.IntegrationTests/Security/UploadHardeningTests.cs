using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using EKA.Application.Abstractions.Ingestion;
using EKA.Infrastructure;
using EKA.Infrastructure.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace EKA.IntegrationTests.Security;

/// <summary>
/// The real extractors against hostile files (Step 12). Uploads are untrusted input, and the
/// parsers are the attack surface they reach.
/// </summary>
public sealed class UploadHardeningTests
{
    private static ITextExtractor ExtractorFor(DocumentFileType type, Action<ExtractionLimits> limits)
    {
        var services = new ServiceCollection().AddLogging().AddTextExtraction();
        services.Configure(limits);
        return services.BuildServiceProvider().GetServices<ITextExtractor>().First(extractor => extractor.CanExtract(type));
    }

    /// <summary>A real .docx with an extra part of <paramref name="paddingBytes"/> zeros: tiny on disk, large in memory.</summary>
    private static MemoryStream WordWithPadding(int paddingBytes)
    {
        var stream = new MemoryStream();
        using (var word = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            main.Document = new WordDocument(new Body(new Paragraph(new Run(new Text("Annual leave is 25 days.")))));

            if (paddingBytes > 0)
            {
                var padding = main.AddNewPart<CustomXmlPart>("application/xml", "rIdPadding");
                using var part = padding.GetStream();
                part.Write(new byte[paddingBytes]);
            }
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task A_docx_that_expands_past_the_limit_is_refused_by_measuring_not_by_trusting_its_headers()
    {
        // 8 MB of zeros compresses to a few kilobytes: the shape of a zip bomb.
        using var bomb = WordWithPadding(8 * 1024 * 1024);
        bomb.Length.ShouldBeLessThan(100_000);
        var extractor = ExtractorFor(DocumentFileType.Docx, limits => limits.MaxDocxUncompressedBytes = 1024 * 1024);

        var thrown = await Should.ThrowAsync<UnreadableDocumentException>(() => extractor.ExtractAsync(bomb));

        thrown.Message.ShouldContain("zip bomb");
    }

    [Fact]
    public async Task An_ordinary_docx_passes_the_guard_and_is_extracted()
    {
        using var ordinary = WordWithPadding(0);
        var extractor = ExtractorFor(DocumentFileType.Docx, _ => { });

        var extracted = await extractor.ExtractAsync(ordinary);

        extracted.Sections.ShouldHaveSingleItem().Text.ShouldContain("25 days");
    }

    [Fact]
    public async Task A_pdf_with_more_pages_than_the_limit_is_refused_before_its_pages_are_parsed()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var page = 1; page <= 3; page++)
        {
            builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText($"Page {page}", 12, new PdfPoint(50, 750), font);
        }

        using var pdf = new MemoryStream(builder.Build());
        var extractor = ExtractorFor(DocumentFileType.Pdf, limits => limits.MaxPdfPages = 2);

        var thrown = await Should.ThrowAsync<UnreadableDocumentException>(() => extractor.ExtractAsync(pdf));

        thrown.Message.ShouldContain("3 pages");
    }
}
