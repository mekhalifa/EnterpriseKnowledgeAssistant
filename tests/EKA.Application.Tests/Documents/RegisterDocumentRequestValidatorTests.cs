using EKA.Application.Documents.RegisterDocument;
using EKA.Contracts.Documents;
using EKA.Domain.Ingestion;
using Shouldly;

namespace EKA.Application.Tests.Documents;

/// <summary>
/// The edge rules — what gets a 400 before any handler is constructed.
/// </summary>
public sealed class RegisterDocumentRequestValidatorTests
{
    private readonly RegisterDocumentRequestValidator _validator = new();

    private static RegisterDocumentRequest Request(
        string title = "Employee handbook",
        string fileName = "handbook.pdf",
        string contentType = "application/pdf",
        long sizeBytes = 2048,
        string? contentHash = null,
        int? authority = null) =>
        new(title, fileName, contentType, sizeBytes, contentHash ?? TestData.Hash, authority);

    [Fact]
    public void A_well_formed_request_passes()
    {
        _validator.Validate(Request()).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_title_fails_naming_the_field(string title)
    {
        var result = _validator.Validate(Request(title: title));

        result.IsValid.ShouldBeFalse();

        // The property name is the whole reason this duplicates a domain rule: a client
        // form can highlight the box, which a DomainException's message cannot tell it.
        result.Errors.ShouldContain(failure => failure.PropertyName == "Title");
    }

    [Fact]
    public void A_title_past_the_domain_limit_fails_here_too()
    {
        var result = _validator.Validate(Request(title: new string('x', DocumentTitle.MaxLength + 1)));

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_title_exactly_at_the_limit_is_accepted()
    {
        // Boundary, not a round number: off-by-one in a length check is the single most
        // common validation bug, and it only ever shows up at the boundary.
        var result = _validator.Validate(Request(title: new string('x', DocumentTitle.MaxLength)));

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("application")]
    [InlineData("application/")]
    [InlineData("/pdf")]
    [InlineData("application pdf")]
    public void Something_that_is_not_a_mime_type_fails(string contentType)
    {
        _validator.Validate(Request(contentType: contentType)).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("text/markdown")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public void Real_mime_types_pass(string contentType)
    {
        _validator.Validate(Request(contentType: contentType)).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_size_fails(long sizeBytes)
    {
        _validator.Validate(Request(sizeBytes: sizeBytes)).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("tooshort")]
    [InlineData("zzzz456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")] // non-hex
    public void A_malformed_content_hash_fails(string contentHash)
    {
        var result = _validator.Validate(Request(contentHash: contentHash));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(failure => failure.PropertyName == "ContentHash");
    }

    [Fact]
    public void An_uppercase_hash_is_accepted_and_normalized_by_the_domain()
    {
        // Rejecting it would be hostile: an uppercase digest is the same digest, and
        // FileMetadata lowercases it on the way in.
        _validator.Validate(Request(contentHash: TestData.Hash.ToUpperInvariant()))
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public void An_omitted_authority_is_valid_because_null_means_use_the_default()
    {
        _validator.Validate(Request(authority: null)).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void An_authority_outside_the_range_fails(int authority)
    {
        _validator.Validate(Request(authority: authority)).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData(AuthorityScore.MinValue)]
    [InlineData(AuthorityScore.MaxValue)]
    public void Authority_at_either_boundary_is_accepted(int authority)
    {
        _validator.Validate(Request(authority: authority)).IsValid.ShouldBeTrue();
    }
}
