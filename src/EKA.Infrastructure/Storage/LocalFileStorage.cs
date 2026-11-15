using EKA.Application.Abstractions.Ingestion;
using Microsoft.Extensions.Options;

namespace EKA.Infrastructure.Storage;

/// <summary>The <c>Storage</c> configuration section.</summary>
public sealed class FileStorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Absolute folder holding every stored file. Empty means
    /// <c>%LOCALAPPDATA%/EKA/files</c> (or <c>~/.local/share/EKA/files</c>).
    /// </summary>
    /// <remarks>
    /// <b>Why not a path relative to the project:</b> the API and the worker must see the
    /// <i>same</i> folder, and "relative" means relative to each process's own working
    /// directory. That differs between <c>dotnet run</c>, Visual Studio and a container. A
    /// per-user application-data folder is the same for both processes on one machine with no
    /// configuration. In a container, set <c>Storage__RootPath</c> to a volume both mount.
    /// </remarks>
    public string RootPath { get; set; } = string.Empty;

    internal string ResolvedRoot => Path.GetFullPath(
        string.IsNullOrWhiteSpace(RootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EKA", "files")
            : RootPath);
}

/// <summary>
/// <see cref="IFileStorage"/> on the local file system (ADR-0021). The second adapter, Azure
/// Blob, arrives with deployment in Step 14.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomic writes:</b> content goes to a uniquely named temporary file in the target
/// folder, then <see cref="File.Move(string, string, bool)"/> replaces the destination in
/// one step. A crash mid-copy leaves a stray <c>.tmp</c>, never a truncated file under a
/// real key. The temp file sits in the same folder because a move across volumes is a copy,
/// and a copy is not atomic.
/// </para>
/// <para>
/// <b>Containment:</b> every key is resolved to a full path and checked to lie under the
/// root. Keys come from <c>StorageKeys</c> and are already safe, but this adapter does not
/// assume its callers are. A key like <c>../../web.config</c> is refused here regardless of
/// what produced it.
/// </para>
/// </remarks>
internal sealed class LocalFileStorage(IOptions<FileStorageOptions> options) : IFileStorage
{
    private const int BufferSize = 81920;

    private readonly string _root = options.Value.ResolvedRoot;

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var destination = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(file, BufferSize, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            // After a successful move this file no longer exists. After a failure it holds
            // a partial write that nobody may ever read.
            File.Delete(temporary);
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Nothing is stored under '{key}'.", key);
        }

        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var full = Path.GetFullPath(Path.Combine(_root, key));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Storage key '{key}' resolves outside the storage root.", nameof(key));
        }

        return full;
    }
}
