using EKA.Application.Abstractions.Ingestion;
using EKA.Domain.Common;

namespace EKA.Application.Tests.Fakes;

/// <summary>In-memory storage with the real adapter's atomic-write contract: a save that throws
/// stores nothing.</summary>
internal sealed class FakeFileStorage : IFileStorage
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        _files[key] = buffer.ToArray();
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
        _files.TryGetValue(key, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new FileNotFoundException(key);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _files.Remove(key);
        return Task.CompletedTask;
    }

    public void Put(string key, byte[] bytes) => _files[key] = bytes;
}

internal sealed class FakeIngestionQueue : IIngestionQueue
{
    public List<(TenantId Tenant, Guid DocumentId)> Enqueued { get; } = [];

    /// <summary>Simulates the job store being unreachable.</summary>
    public bool Fail { get; set; }

    public Task<bool> EnqueueExtractionAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            return Task.FromResult(false);
        }

        Enqueued.Add((tenantId, documentId));
        return Task.FromResult(true);
    }
}

/// <summary>Claims one type and returns a fixed result, or throws a given exception.</summary>
internal sealed class FakeExtractor(DocumentFileType handles, Func<ExtractedDocument> result) : ITextExtractor
{
    public int Calls { get; private set; }

    public bool CanExtract(DocumentFileType fileType) => fileType == handles;

    public Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(result());
    }

    public static FakeExtractor Returning(DocumentFileType handles, params string[] sectionTexts) =>
        new(handles, () => new ExtractedDocument([.. sectionTexts.Select((text, i) => new ExtractedSection(i + 1, null, text))]));

    public static FakeExtractor Throwing(DocumentFileType handles, Exception exception) =>
        new(handles, () => throw exception);
}
