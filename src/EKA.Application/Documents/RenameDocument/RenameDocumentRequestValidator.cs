using EKA.Contracts.Documents;
using EKA.Domain.Ingestion;
using FluentValidation;

namespace EKA.Application.Documents.RenameDocument;

/// <summary>Shape checks for <see cref="RenameDocumentRequest"/>.</summary>
public sealed class RenameDocumentRequestValidator : AbstractValidator<RenameDocumentRequest>
{
    public RenameDocumentRequestValidator() =>
        RuleFor(request => request.Title)
            .NotEmpty()
            .MaximumLength(DocumentTitle.MaxLength);
}
