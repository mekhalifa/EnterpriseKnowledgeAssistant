using EKA.Application.Abstractions;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Common;
using EKA.Contracts.Documents;
using EKA.Domain.Access;
using EKA.Domain.Common;
using FluentValidation;

namespace EKA.Application.Documents.SetDocumentAccess;

/// <summary>Replaces who may read a document.</summary>
/// <param name="Caller">Who is asking. They must be able to read the document now.</param>
/// <param name="DocumentId">The document.</param>
/// <param name="Principals">Canonical keys, already shape-checked by the validator.</param>
public sealed record SetDocumentAccessCommand(AccessScope Caller, Guid DocumentId, IReadOnlyList<string> Principals);

/// <summary>
/// Changes a document's access list in SQL Server and in the vector store's copy of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who may change access: the document's owner, or a tenant administrator</b> (Step 12,
/// ADR-0031). A reader who is neither gets 403. A caller who cannot see the document at all
/// gets 404, so they cannot grant themselves access to something that, to them, does not exist.
/// An administrator can also rescue a document whose list locks everyone out.
/// </para>
/// <para>
/// <b>Two stores, and this time the copy goes first.</b> The opposite of delete, and for the
/// same reason: choose the order whose every failure is safe. SQL Server decides; the vector
/// payload is a copy used to filter <i>before</i> SQL is asked.
/// </para>
/// <list type="bullet">
/// <item><description><b>Narrowing</b> (removing Bob): the payload loses Bob first, so the search
/// stops finding the document for him; then SQL. If the SQL save fails, SQL still says "Bob"
/// while the payload says no: Bob misses the document, which is safe, and a retry
/// fixes it.</description></item>
/// <item><description><b>Widening</b> (adding Carol): the payload gains Carol first, but SQL
/// still says no, and every hit is re-checked against SQL, so Carol sees nothing until the
/// save commits.</description></item>
/// </list>
/// <para>
/// SQL first would leave, after a failed payload update, a payload that still lists Bob. That
/// too is safe (SQL re-check drops it), but a failed widening would then report success while
/// Carol never finds the document, and nothing would retry it. Copy first means a failure
/// is a 500 the client sees and repeats; the request is idempotent.
/// </para>
/// </remarks>
public sealed class SetDocumentAccessHandler(
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    IVectorStore vectors,
    TimeProvider timeProvider)
{
    public async Task<Result<DocumentResponse>> HandleAsync(SetDocumentAccessCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var document = await documents
            .GetByIdAsync(command.Caller.TenantId, command.DocumentId, cancellationToken)
            .ConfigureAwait(false);

        if (DocumentAuthorization.CannotManage(document, command.Caller, command.DocumentId, out var error))
        {
            return error;
        }

        DocumentAccess access;
        try
        {
            access = DocumentAccess.Of(command.Principals.Select(AccessPrincipal.Parse));
        }
        catch (DomainException exception)
        {
            // The validator checks the same rules at the edge. This covers callers that are
            // not the HTTP endpoint, and keeps an invariant violation from becoming a 500.
            return DocumentErrors.InvalidAccess(exception.Message);
        }

        await vectors.SetDocumentAccessAsync(document.TenantId, document.Id, access.Keys, cancellationToken).ConfigureAwait(false);

        document.ChangeAccess(access, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return document.ToResponse(command.Caller);
    }
}

/// <summary>Shape checks for <see cref="SetDocumentAccessRequest"/>: the domain's rules, asked without I/O.</summary>
public sealed class SetDocumentAccessRequestValidator : AbstractValidator<SetDocumentAccessRequest>
{
    public SetDocumentAccessRequestValidator()
    {
        RuleFor(request => request.Principals)
            .NotNull()
            .NotEmpty().WithMessage("Grant 'tenant', or at least one 'user:<subject>' or 'group:<id>'.")
            .Must(principals => principals.Count <= DocumentAccess.MaxPrincipals)
            .WithMessage($"At most {DocumentAccess.MaxPrincipals} principals; grant a group instead.");

        RuleForEach(request => request.Principals)
            .Must(key => AccessPrincipal.TryParse(key, out _))
            .WithMessage("'{PropertyValue}' is not 'tenant', 'user:<subject>' or 'group:<id>'.");

        RuleFor(request => request.Principals)
            .Must(principals => !(principals.Contains("tenant") && principals.Distinct(StringComparer.Ordinal).Count() > 1))
            .When(request => request.Principals is not null)
            .WithMessage("'tenant' already grants everyone in the tenant and cannot be combined with users or groups.");
    }
}
