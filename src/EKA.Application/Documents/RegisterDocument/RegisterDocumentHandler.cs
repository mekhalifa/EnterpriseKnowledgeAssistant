using EKA.Application.Abstractions;
using EKA.Application.Common;
using EKA.Contracts.Documents;
using EKA.Domain.Ingestion;

namespace EKA.Application.Documents.RegisterDocument;

/// <summary>
/// What registration produced, and whether it had already happened.
/// </summary>
/// <param name="Document">The document, newly created or previously registered.</param>
/// <param name="AlreadyRegistered">
/// True when the same bytes were already registered, so the endpoint answers 200 OK instead
/// of 201 Created.
/// </param>
/// <remarks>
/// An application-layer type, not a contract: the flag exists to pick a status code, and a
/// client that reads the status code does not also need it in the body.
/// </remarks>
public sealed record RegisterDocumentOutcome(DocumentResponse Document, bool AlreadyRegistered);

/// <summary>
/// Creates a <see cref="Document"/> in <see cref="IngestionStatus.Pending"/>, or returns the
/// existing one when these exact bytes were registered before.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent by content hash.</b> Uploads get retried — a flaky network, an impatient
/// user, a job that re-runs. Without this check the same file becomes two documents, and
/// every subsequent search returns each passage twice while costing twice the embedding
/// spend. Returning the original is the behaviour the caller wanted anyway.
/// </para>
/// <para>
/// <b>The race this does not close.</b> Two concurrent registrations of identical bytes can
/// both read "not found" and both insert. The closing move is a unique index on
/// <c>(TenantId, ContentHash)</c>, which Step 7 adds along with real uploads; today a
/// duplicate costs a wasted row rather than a correctness failure, and inventing a
/// migration for a path nothing yet exercises would be building on a guess. Recorded as a
/// known limitation in docs/PROGRESS.md.
/// </para>
/// <para>
/// <b>Time comes from <see cref="TimeProvider"/>, not the clock.</b> The domain refuses to
/// read the clock (ADR-0008), so this layer is where "now" enters the system — which also
/// means a test can register a document at an exact instant and assert on it.
/// </para>
/// </remarks>
public sealed class RegisterDocumentHandler(
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<RegisterDocumentOutcome>> HandleAsync(
        RegisterDocumentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var existing = await documents
            .FindByContentHashAsync(command.TenantId, command.ContentHash, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return new RegisterDocumentOutcome(existing.ToResponse(), AlreadyRegistered: true);
        }

        var now = timeProvider.GetUtcNow();

        // Version 7, not version 4. A v7 GUID starts with a millisecond timestamp, so
        // consecutive inserts land next to each other in SQL Server's clustered index
        // instead of scattering across it. Random v4 keys fragment the index and turn an
        // append-only table into a page-splitting one, which is a real cost on a table
        // that only ever grows.
        var documentId = Guid.CreateVersion7();

        var file = new FileMetadata(
            command.FileName,
            command.ContentType,
            command.SizeBytes,
            command.ContentHash);

        var document = new Document(
            documentId,
            command.TenantId,
            new DocumentTitle(command.Title),
            file,
            StorageKeys.For(command.TenantId, documentId, command.FileName),
            now,
            command.Authority is null ? null : new AuthorityScore(command.Authority.Value));

        documents.Add(document);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new RegisterDocumentOutcome(document.ToResponse(), AlreadyRegistered: false);
    }
}
