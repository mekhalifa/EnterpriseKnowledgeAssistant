using EKA.Contracts.Documents;
using EKA.Domain.Ingestion;
using FluentValidation;

namespace EKA.Application.Documents.UploadDocument;

/// <summary>Shape checks for the upload's form fields (ADR-0013). The bytes are checked by
/// the handler, because only reading them can tell what they are.</summary>
public sealed class UploadDocumentMetadataValidator : AbstractValidator<UploadDocumentMetadata>
{
    public UploadDocumentMetadataValidator()
    {
        RuleFor(metadata => metadata.Title)
            .MaximumLength(DocumentTitle.MaxLength)
            .Must(title => !string.IsNullOrWhiteSpace(title))
            .When(metadata => metadata.Title is not null)
            .WithMessage("Title, when given, cannot be blank.");

        RuleFor(metadata => metadata.Authority!.Value)
            .InclusiveBetween(AuthorityScore.MinValue, AuthorityScore.MaxValue)
            .When(metadata => metadata.Authority is not null);
    }
}
