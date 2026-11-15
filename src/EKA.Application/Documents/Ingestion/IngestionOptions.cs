using Microsoft.Extensions.Options;

namespace EKA.Application.Documents.Ingestion;

/// <summary>The <c>Ingestion</c> configuration section, read by both the API and the worker.</summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>
    /// Largest accepted upload. 25 MB fits a several-hundred-page text PDF; above that is
    /// almost always a scan, which extraction cannot read anyway (ADR-0022). The API's
    /// request-size limit is set from this, so an oversized body is cut off while it streams
    /// in, never buffered whole.
    /// </summary>
    public long MaxUploadBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>
    /// How long a document may sit in Pending before the sweep assumes its hand-off was lost
    /// and enqueues it again. Comfortably longer than a normal queue wait, so the sweep rarely
    /// duplicates a job that was only slow. A duplicate is harmless, merely wasted work.
    /// </summary>
    public TimeSpan StalledAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Documents re-enqueued per sweep. Bounds the burst after a long queue outage.</summary>
    public int SweepBatchSize { get; set; } = 100;
}

internal sealed class IngestionOptionsValidator : IValidateOptions<IngestionOptions>
{
    public ValidateOptionsResult Validate(string? name, IngestionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var section = IngestionOptions.SectionName;

        if (options.MaxUploadBytes is < 1 or > 512L * 1024 * 1024)
        {
            failures.Add($"{section}:MaxUploadBytes must be between 1 byte and 512 MB.");
        }

        if (options.StalledAfter < TimeSpan.FromSeconds(30))
        {
            failures.Add($"{section}:StalledAfter must be at least 30 seconds, or the sweep races normal hand-offs.");
        }

        if (options.SweepBatchSize is < 1 or > 10_000)
        {
            failures.Add($"{section}:SweepBatchSize must be between 1 and 10000.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
