using System.Text.Json;
using EKA.Application.Abstractions.Ingestion;
using EKA.Domain.Ingestion;

namespace EKA.Application.Documents.Ingestion;

/// <summary>
/// Reads and writes a document's extracted text as a JSON artifact next to the original
/// file (ADR-0023).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a file and not rows.</b> Extracted text is large, written once per extraction and
/// read once by the chunker (Step 8), which then stores the text again as chunks. Keeping it
/// in SQL would put every document's text in the database twice. As an artifact it is
/// replaced whole on re-extraction, which makes re-running extraction idempotent for free.
/// </para>
/// <para>
/// <b>Versioned format.</b> <see cref="ExtractedTextArtifact.FormatVersion"/> lets a later
/// chunker refuse, or migrate, an artifact written by an older extractor, instead of
/// misreading it.
/// </para>
/// </remarks>
public sealed class ExtractedTextStore(IFileStorage storage)
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string KeyFor(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return $"{document.StorageKey}.extracted.json";
    }

    public async Task SaveAsync(Document document, ExtractedDocument extracted, DateTimeOffset extractedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extracted);

        var artifact = new ExtractedTextArtifact(
            CurrentFormatVersion,
            document.Id,
            document.File.ContentHash,
            extractedAt,
            extracted.Sections);

        using var buffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(buffer, artifact, Json, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        await storage.SaveAsync(KeyFor(document), buffer, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExtractedTextArtifact> LoadAsync(Document document, CancellationToken cancellationToken = default)
    {
        await using var stream = await storage.OpenReadAsync(KeyFor(document), cancellationToken).ConfigureAwait(false);

        return await JsonSerializer.DeserializeAsync<ExtractedTextArtifact>(stream, Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Extracted-text artifact for document {document.Id} is empty.");
    }
}

/// <param name="ContentHash">The SHA-256 of the file this was extracted from, so a reader
/// can tell an artifact from a stale extraction of different bytes.</param>
public sealed record ExtractedTextArtifact(
    int FormatVersion,
    Guid DocumentId,
    string ContentHash,
    DateTimeOffset ExtractedAt,
    IReadOnlyList<ExtractedSection> Sections);
