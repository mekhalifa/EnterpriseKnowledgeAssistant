using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using EKA.Domain.Retrieval;
using Shouldly;

namespace EKA.Domain.Tests;

public sealed class ValueObjectTests
{
    // ── Value equality: the defining property ───────────────────────────────────────

    [Fact]
    public void Value_objects_are_equal_when_their_values_match()
    {
        // Unlike entities, which are equal only when they are the same thing, two value
        // objects holding the same data ARE the same value. This is why they can be
        // freely copied and compared without identity tracking.
        new DocumentTitle("Handbook").ShouldBe(new DocumentTitle("Handbook"));
        new ChunkIndex(7).ShouldBe(new ChunkIndex(7));
        new TokenCount(42).ShouldBe(new TokenCount(42));
        new PageRange(2, 5).ShouldBe(new PageRange(2, 5));
        TestData.Pdf().ShouldBe(TestData.Pdf());
    }

    // ── The struct-default trap ─────────────────────────────────────────────────────

    [Fact]
    public void Struct_value_objects_can_bypass_validation_via_default()
    {
        // C# gives every struct an implicit parameterless constructor that a value object
        // cannot intercept. TenantId's constructor rejects Guid.Empty, yet `default`
        // produces exactly that — no exception, no guard, an invalid value in hand.
        //
        // This is NOT a bug in TenantId; it is a property of the language. It is why
        // aggregates re-check with IsEmpty rather than trusting the type, and why value
        // objects whose payload must be non-null are classes instead.
        var bypassed = default(TenantId);

        bypassed.Value.ShouldBe(Guid.Empty);
        bypassed.IsEmpty.ShouldBeTrue();
        Should.Throw<DomainException>(() => new TenantId(Guid.Empty));
    }

    [Fact]
    public void Struct_defaults_are_harmless_where_zero_is_a_legitimate_value()
    {
        // The same trap is benign for these: index 0 is the first chunk, zero tokens is a
        // real count, and zero authority means "least trusted" — the safe direction.
        default(ChunkIndex).Value.ShouldBe(0);
        default(TokenCount).Value.ShouldBe(0);
        default(AuthorityScore).Value.ShouldBe(0);
        default(SimilarityScore).Value.ShouldBe(0d);
    }

    // ── DocumentTitle ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_title_cannot_be_blank(string value)
    {
        Should.Throw<DomainException>(() => new DocumentTitle(value));
    }

    [Fact]
    public void A_title_is_trimmed()
    {
        new DocumentTitle("  Employee Handbook  ").Value.ShouldBe("Employee Handbook");
    }

    [Fact]
    public void A_title_is_length_bounded()
    {
        Should.NotThrow(() => new DocumentTitle(new string('x', DocumentTitle.MaxLength)));
        Should.Throw<DomainException>(
            () => new DocumentTitle(new string('x', DocumentTitle.MaxLength + 1)));
    }

    [Fact]
    public void A_title_is_measured_after_trimming()
    {
        var padded = $"  {new string('x', DocumentTitle.MaxLength)}  ";

        Should.NotThrow(() => new DocumentTitle(padded));
    }

    // ── ChunkIndex ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_chunk_index_cannot_be_negative()
    {
        Should.Throw<DomainException>(() => new ChunkIndex(-1));
    }

    [Fact]
    public void Chunk_indexes_compare_and_advance()
    {
        ChunkIndex.First.Value.ShouldBe(0);
        ChunkIndex.First.Next().Value.ShouldBe(1);
        (new ChunkIndex(1) < new ChunkIndex(2)).ShouldBeTrue();
        (new ChunkIndex(5) >= new ChunkIndex(5)).ShouldBeTrue();
    }

    [Fact]
    public void Chunk_indexes_sort_numerically()
    {
        List<ChunkIndex> indexes = [new(10), new(2), new(1)];

        indexes.Order().Select(i => i.Value).ShouldBe([1, 2, 10]);
    }

    // ── TokenCount ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_token_count_cannot_be_negative()
    {
        Should.Throw<DomainException>(() => new TokenCount(-1));
    }

    [Fact]
    public void Token_counts_add()
    {
        (new TokenCount(100) + new TokenCount(250)).Value.ShouldBe(350);
    }

    // ── AuthorityScore ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Authority_is_bounded_zero_to_one_hundred(int value)
    {
        Should.Throw<DomainException>(() => new AuthorityScore(value));
    }

    [Fact]
    public void Authority_presets_order_correctly()
    {
        (AuthorityScore.Unverified < AuthorityScore.Standard).ShouldBeTrue();
        (AuthorityScore.Standard < AuthorityScore.Authoritative).ShouldBeTrue();
    }

    // ── PageRange ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Pages_are_one_based()
    {
        Should.Throw<DomainException>(() => new PageRange(0, 5));
    }

    [Fact]
    public void A_page_range_cannot_end_before_it_starts()
    {
        Should.Throw<DomainException>(() => new PageRange(10, 4));
    }

    [Fact]
    public void A_page_range_reports_its_span()
    {
        var range = new PageRange(12, 14);

        range.PageCount.ShouldBe(3);
        range.IsSinglePage.ShouldBeFalse();
        range.ToString().ShouldBe("pp. 12-14");

        PageRange.SinglePage(7).IsSinglePage.ShouldBeTrue();
        PageRange.SinglePage(7).ToString().ShouldBe("p. 7");
    }

    // ── FileMetadata ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_file_must_have_positive_size()
    {
        Should.Throw<DomainException>(
            () => new FileMetadata("a.pdf", "application/pdf", 0, TestData.Sha256));
    }

    [Fact]
    public void Content_type_is_lowercased()
    {
        var file = new FileMetadata("a.pdf", "Application/PDF", 10, TestData.Sha256);

        file.ContentType.ShouldBe("application/pdf");
    }

    [Theory]
    [InlineData("tooshort")]
    [InlineData("zzzz0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void A_content_hash_must_be_a_hex_sha256(string hash)
    {
        Should.Throw<DomainException>(
            () => new FileMetadata("a.pdf", "application/pdf", 10, hash));
    }

    [Fact]
    public void An_uppercase_content_hash_is_normalized_rather_than_rejected()
    {
        var file = new FileMetadata(
            "a.pdf", "application/pdf", 10, TestData.Sha256.ToUpperInvariant());

        file.ContentHash.ShouldBe(TestData.Sha256);
    }

    [Fact]
    public void FileMetadata_imposes_no_maximum_size()
    {
        // Upload limits are environment configuration, not a truth about files. Baking a
        // ceiling in here would make a domain test fail when Ops raises the limit.
        Should.NotThrow(
            () => new FileMetadata("huge.pdf", "application/pdf", long.MaxValue, TestData.Sha256));
    }

    // ── SimilarityScore ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void A_similarity_score_must_be_normalized(double value)
    {
        Should.Throw<DomainException>(() => new SimilarityScore(value));
    }

    [Fact]
    public void NaN_is_rejected()
    {
        Should.Throw<DomainException>(() => new SimilarityScore(double.NaN));
        Should.Throw<DomainException>(() => SimilarityScore.FromCosineSimilarity(double.NaN));
    }

    [Theory]
    [InlineData(1.0, 1.0)]    // identical direction
    [InlineData(0.0, 0.5)]    // orthogonal
    [InlineData(-1.0, 0.0)]   // opposite
    public void Cosine_similarity_is_mapped_onto_zero_to_one(double cosine, double expected)
    {
        // Providers disagree on what "score" means: cosine runs -1..1, cosine distance
        // runs 0..2 and is better when lower. Normalizing at the boundary stops a
        // configured threshold from silently inverting when the metric changes.
        SimilarityScore.FromCosineSimilarity(cosine).Value.ShouldBe(expected, tolerance: 1e-9);
    }

    [Fact]
    public void Out_of_range_cosine_values_are_clamped_not_rejected()
    {
        // Floating-point error can push a provider's cosine marginally outside [-1, 1].
        // Throwing on 1.0000000002 would fail a query for no reason.
        SimilarityScore.FromCosineSimilarity(1.0000000002).Value.ShouldBe(1d, tolerance: 1e-9);
    }

    [Fact]
    public void Similarity_scores_sort_so_the_best_match_can_be_found()
    {
        List<SimilarityScore> scores = [new(0.2), new(0.9), new(0.5)];

        scores.Max().Value.ShouldBe(0.9, tolerance: 1e-9);
    }
}
