using EKA.Contracts.Documents;
using EKA.Domain.Ingestion;
using FluentValidation;

namespace EKA.Application.Documents.RegisterDocument;

/// <summary>
/// Shape checks for <see cref="RegisterDocumentRequest"/>, run before any handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Doesn't the domain already validate this?</b> Yes, and it still will — the rules here
/// are a deliberate second copy of what <c>DocumentTitle</c> and <c>FileMetadata</c>
/// enforce. The difference is who the message is for. A <c>DomainException</c> says "a
/// programmer sent me nonsense" and belongs in a log; it carries no field name, so the
/// client cannot highlight the box that was wrong. These rules produce a 400 with
/// <c>{"ContentHash": ["must be a 64-character lowercase hex SHA-256 digest"]}</c>, which a
/// form can act on.
/// </para>
/// <para>
/// The duplication is bounded and intentional: the domain keeps the authority, this keeps
/// the ergonomics. If they ever disagree the domain wins — it throws, the request fails,
/// and the bug is loud rather than silent. What must never happen is the reverse: a rule
/// here that the domain does not also enforce, because then the worker and the tests can
/// create states the API rejects.
/// </para>
/// <para>
/// <b>No I/O in a validator.</b> "Has this hash been seen before?" needs the database and
/// lives in the handler, returning a <c>Result</c>. A validator that queries is a validator
/// that cannot be unit tested, runs before authorization has been applied, and silently
/// doubles the request's database round trips.
/// </para>
/// </remarks>
public sealed class RegisterDocumentRequestValidator : AbstractValidator<RegisterDocumentRequest>
{
    /// <summary>Length of a lowercase hex SHA-256 digest.</summary>
    private const int Sha256HexLength = 64;

    public RegisterDocumentRequestValidator()
    {
        RuleFor(request => request.Title)
            .NotEmpty()
            .MaximumLength(DocumentTitle.MaxLength);

        RuleFor(request => request.FileName)
            .NotEmpty()
            .MaximumLength(260); // The longest path component Windows and most object stores accept.

        RuleFor(request => request.ContentType)
            .NotEmpty()
            .MaximumLength(255)
            .Matches("^[a-zA-Z0-9!#$&^_.+-]+/[a-zA-Z0-9!#$&^_.+-]+$")
            .WithMessage("'{PropertyName}' must be a MIME type such as 'application/pdf'.");

        RuleFor(request => request.SizeBytes)
            .GreaterThan(0);

        RuleFor(request => request.ContentHash)
            .NotEmpty()
            .Length(Sha256HexLength)
            .Matches("^[a-fA-F0-9]+$")
            .WithMessage("'{PropertyName}' must be a hex SHA-256 digest.");

        // Null means "use the default", which is a legitimate request rather than a
        // missing value — so the range rule applies only when a number was actually sent.
        RuleFor(request => request.Authority!.Value)
            .InclusiveBetween(AuthorityScore.MinValue, AuthorityScore.MaxValue)
            .When(request => request.Authority is not null);
    }
}
