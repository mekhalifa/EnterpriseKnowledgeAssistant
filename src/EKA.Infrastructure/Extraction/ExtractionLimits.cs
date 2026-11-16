using System.IO.Compression;
using EKA.Application.Abstractions.Ingestion;
using Microsoft.Extensions.Logging;

namespace EKA.Infrastructure.Extraction;

/// <summary>The <c>Extraction</c> section: limits that keep a hostile file from exhausting the worker (Step 12).</summary>
public sealed class ExtractionLimits
{
    public const string SectionName = "Extraction";

    /// <summary>Pages read from one PDF. Each page costs parse time and memory; real policies run to hundreds.</summary>
    public int MaxPdfPages { get; set; } = 2_000;

    /// <summary>Bytes a DOCX may <i>actually</i> decompress to, measured, not as declared.</summary>
    public long MaxDocxUncompressedBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>Entries in a DOCX package. A real document has dozens.</summary>
    public int MaxDocxEntries { get; set; } = 2_000;
}

/// <summary>
/// Measures what a ZIP package (DOCX) really decompresses to before the Open XML SDK opens it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why measure instead of reading the sizes.</b> A ZIP's central directory declares every
/// entry's uncompressed size, and a zip bomb lies about it: 40 KB that claims 1 KB and inflates
/// to gigabytes. So each entry is decompressed into nothing, with a running count that stops
/// the moment the limit is passed. The cost is decompressing twice, bounded by the limit. The
/// alternative is trusting the attacker's arithmetic.
/// </para>
/// </remarks>
internal static class ZipBombGuard
{
    public static void Check(Stream content, ExtractionLimits limits)
    {
        long total = 0;
        var buffer = new byte[81920];

        using (var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (archive.Entries.Count > limits.MaxDocxEntries)
            {
                throw new UnreadableDocumentException(
                    $"The Word document has {archive.Entries.Count} internal parts; the limit is {limits.MaxDocxEntries}.");
            }

            foreach (var entry in archive.Entries)
            {
                using var stream = entry.Open();
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > limits.MaxDocxUncompressedBytes)
                    {
                        throw new UnreadableDocumentException(
                            $"The Word document expands to more than {limits.MaxDocxUncompressedBytes / (1024 * 1024)} MB; it may be a zip bomb.");
                    }
                }
            }
        }

        content.Position = 0;
    }
}

/// <summary>
/// The scanner adapter used until a real one is configured: everything is clean, and the log
/// says so once per process.
/// </summary>
/// <remarks>
/// It exists so that "no scanner" is an explicit, visible configuration, not a missing
/// registration. Replacing it with ClamAV or a cloud scanner is one class and one line in
/// <c>DependencyInjection</c>.
/// </remarks>
internal sealed partial class NoMalwareScanner(ILogger<NoMalwareScanner> logger) : IMalwareScanner
{
    private int _warned;

    public Task<ScanVerdict> ScanAsync(Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            LogNoScanner(logger);
        }

        return Task.FromResult(ScanVerdict.Clean);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No malware scanner is configured: uploaded files are parsed unscanned. Register an IMalwareScanner adapter for production.")]
    private static partial void LogNoScanner(ILogger logger);
}
