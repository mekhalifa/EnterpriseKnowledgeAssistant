using EKA.Application.Abstractions;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Common;
using EKA.Application.Documents.Ingestion;
using EKA.Domain.Ingestion;
using Microsoft.Extensions.Options;

namespace EKA.Application.Documents.UploadDocument;

/// <summary>
/// Accepts a file: detects what it really is, stores it while hashing it, records the
/// document, and hands it to the worker.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order of the three writes, and what each failure leaves behind:</b>
/// </para>
/// <list type="number">
/// <item><description><b>File stored.</b> If the insert then fails, the handler deletes it.
/// If the process dies before that delete, an orphan file is left with no row pointing at
/// it. That is wasted disk, never a wrong answer.</description></item>
/// <item><description><b>Row inserted.</b> From here the document exists, and it is the
/// source of truth.</description></item>
/// <item><description><b>Job enqueued.</b> If this fails, the document is Pending with no
/// job, and the sweep re-enqueues it after <c>Ingestion:StalledAfter</c>.</description></item>
/// </list>
/// <para>
/// File first, because a row pointing at a missing file is the only order that produces a
/// <i>wrong</i> state: a worker that fails for a reason nobody can fix.
/// </para>
/// <para>
/// <b>Idempotent on content.</b> The same bytes uploaded twice by one tenant return the first
/// document (200, not 201). The check-then-insert can race, and the unique index
/// <c>UX_Documents_TenantId_ContentHash</c> settles the race. The loser reads the winner and
/// answers exactly as if it had found it first.
/// </para>
/// </remarks>
public sealed class UploadDocumentHandler(
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    IFileStorage storage,
    IIngestionQueue queue,
    IOptions<IngestionOptions> options,
    TimeProvider timeProvider)
{
    private readonly long _maxBytes = options.Value.MaxUploadBytes;

    public async Task<Result<UploadDocumentOutcome>> HandleAsync(
        UploadDocumentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!command.Content.CanSeek)
        {
            throw new ArgumentException("Upload content must be seekable.", nameof(command));
        }

        // Browsers have sent "C:\fakepath\report.pdf", and other clients send relative paths.
        // Only the last segment is a file name; the rest is the client's file system, which is
        // nobody's business and no part of a storage key. Backslashes are folded first, because
        // on Linux Path.GetFileName does not treat a backslash as a separator.
        var fileName = Path.GetFileName(command.FileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return DocumentErrors.UnsupportedFileType(command.FileName);
        }

        var header = new byte[FileTypeDetector.HeaderLength];
        var headerLength = await command.Content
            .ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        command.Content.Position = 0;

        if (headerLength == 0)
        {
            return DocumentErrors.EmptyFile;
        }

        var fileType = FileTypeDetector.Detect(header.AsSpan(0, headerLength), fileName);
        if (fileType is DocumentFileType.Unknown)
        {
            return DocumentErrors.UnsupportedFileType(fileName);
        }

        var tenantId = command.Caller.TenantId;
        var documentId = Guid.CreateVersion7();
        var storageKey = StorageKeys.For(tenantId, documentId, fileName);

        string contentHash;
        long sizeBytes;
        await using (var hashing = new HashingReadStream(command.Content, _maxBytes))
        {
            try
            {
                await storage.SaveAsync(storageKey, hashing, cancellationToken).ConfigureAwait(false);
            }
            catch (FileTooLargeException)
            {
                // The storage write is atomic, so an aborted save left nothing under the key.
                return DocumentErrors.FileTooLarge(_maxBytes);
            }

            contentHash = hashing.Digest;
            sizeBytes = hashing.BytesRead;
        }

        var existing = await documents
            .FindByContentHashAsync(tenantId, contentHash, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            await storage.DeleteAsync(storageKey, cancellationToken).ConfigureAwait(false);
            return AlreadyRegistered(existing, command);
        }

        var document = new Document(
            documentId,
            tenantId,
            new DocumentTitle(command.Title ?? DefaultTitle(fileName)),
            new FileMetadata(fileName, FileTypeDetector.ContentTypeOf(fileType), sizeBytes, contentHash),
            storageKey,
            timeProvider.GetUtcNow(),
            command.Authority is null ? null : new AuthorityScore(command.Authority.Value));

        documents.Add(document);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateKeyException)
        {
            // Lost the race to a concurrent upload of the same bytes. Clean up our copy and
            // answer with theirs.
            await storage.DeleteAsync(storageKey, cancellationToken).ConfigureAwait(false);

            var winner = await documents
                .FindByContentHashAsync(tenantId, contentHash, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Unique index rejected the insert, but no conflicting document exists.");

            return AlreadyRegistered(winner, command);
        }
        catch
        {
            await storage.DeleteAsync(storageKey, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var queued = await queue
            .EnqueueIngestionAsync(document.TenantId, document.Id, cancellationToken)
            .ConfigureAwait(false);

        return new UploadDocumentOutcome(document.ToResponse(), AlreadyRegistered: false, queued);
    }

    /// <summary>
    /// The same bytes are already a document. Returned to a caller who may read it; refused,
    /// without naming it, to one who may not.
    /// </summary>
    /// <remarks>
    /// Content hashes are unique per tenant, so a colleague uploading a file that is already
    /// restricted to HR cannot get a second, readable copy. Returning the existing document
    /// would hand them its id, title and status. A 409 still says "these bytes exist here",
    /// which is a smaller disclosure, and the only one any answer can avoid: creating a
    /// duplicate would break the idempotency index. Recorded in step-09.
    /// </remarks>
    private static Result<UploadDocumentOutcome> AlreadyRegistered(Document existing, UploadDocumentCommand command) =>
        existing.IsAccessibleTo(command.Caller)
            ? new UploadDocumentOutcome(existing.ToResponse(), AlreadyRegistered: true, Queued: false)
            : DocumentErrors.DuplicateNotAccessible;

    /// <summary>"Leave Policy 2026.pdf" becomes "Leave Policy 2026", cut to the title limit.</summary>
    private static string DefaultTitle(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (stem.Length == 0)
        {
            stem = "Untitled document";
        }

        return stem.Length <= DocumentTitle.MaxLength ? stem : stem[..DocumentTitle.MaxLength].TrimEnd();
    }
}
