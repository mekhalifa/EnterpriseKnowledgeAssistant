using EKA.Application.Abstractions.AI;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Retrieval;
using EKA.Application.Retrieval.SearchChunks;
using EKA.Application.Tests.Fakes;
using EKA.Contracts.Retrieval;
using EKA.Domain.Access;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace EKA.Application.Tests.Retrieval;

/// <summary>The Specification: what each condition means, and how they compose.</summary>
public sealed class ChunkFilterTests
{
    private static ChunkVector Point(TenantId? tenant = null, string model = "test-model", params string[] access) =>
        new(tenant ?? TestData.Tenant, Guid.NewGuid(), new ChunkIndex(0), new float[4], model, null, null, access.Length == 0 ? ["tenant"] : access);

    [Fact]
    public void The_standard_filter_pins_the_tenant_and_carries_the_callers_principals_and_the_model()
    {
        var filter = ChunkFilter.For(TestData.Caller(subject: "carol", groups: "hr"), "test-model");

        filter.PinnedTenant.ShouldBe(TestData.Tenant);
        filter.Conditions.Count.ShouldBe(3);
        filter.Conditions.OfType<GrantedToAny>().Single().PrincipalKeys.ShouldBe(["tenant", "user:carol", "group:hr"]);
        filter.Conditions.OfType<EmbeddingModelIs>().Single().Model.ShouldBe("test-model");
    }

    [Fact]
    public void Conjunctions_flatten_so_the_tenant_is_always_found_at_the_top()
    {
        var filter = ChunkFilter.EmbeddedWith("m").And(ChunkFilter.GrantedToAnyOf(["tenant"]).And(ChunkFilter.InTenant(TestData.Tenant)));

        filter.Conditions.ShouldAllBe(condition => !(condition is AllOf));
        filter.PinnedTenant.ShouldBe(TestData.Tenant);
    }

    [Fact]
    public void A_filter_without_a_tenant_says_so()
    {
        ChunkFilter.EmbeddedWith("m").And(ChunkFilter.GrantedToAnyOf(["tenant"])).PinnedTenant.ShouldBeNull();
    }

    [Fact]
    public void An_empty_principal_list_is_refused_rather_than_read_as_no_condition()
    {
        Should.Throw<ArgumentException>(() => ChunkFilter.GrantedToAnyOf([]));
    }

    [Fact]
    public void Every_condition_must_hold()
    {
        var filter = ChunkFilter.For(TestData.Caller(subject: "carol", groups: "hr"), "test-model");

        filter.IsSatisfiedBy(Point()).ShouldBeTrue();
        filter.IsSatisfiedBy(Point(access: "group:hr")).ShouldBeTrue();
        filter.IsSatisfiedBy(Point(access: "user:carol")).ShouldBeTrue();
        filter.IsSatisfiedBy(Point(access: "group:finance")).ShouldBeFalse();
        filter.IsSatisfiedBy(Point(TestData.OtherTenant)).ShouldBeFalse();
        filter.IsSatisfiedBy(Point(model: "old-model")).ShouldBeFalse();
    }
}

public sealed class ChunkRetrieverTests
{
    private static readonly float[] Query = [1, 0, 0, 0];
    private static readonly float[] Same = [1, 0, 0, 0];
    private static readonly float[] Close = [1, 1, 0, 0];
    private static readonly float[] Far = [0, 1, 0, 0];

    private readonly FakeVectorStore _vectors = new();
    private readonly FakeChunkReader _chunks = new();
    private readonly FixedQueryEmbedding _embeddings = new(Query);
    private readonly RetrievalOptions _options = new() { DefaultTopK = 5, MaxTopK = 20, MinimumScore = 0.6, CandidateMultiplier = 2 };

    private ChunkRetriever Retriever() =>
        new(_embeddings, _vectors, _chunks, Options.Create(_options), NullLogger<ChunkRetriever>.Instance);

    /// <summary>Indexes a completed document as the worker would: its vectors carry its access keys.</summary>
    private Document Index(Document document, params float[][] vectors)
    {
        _chunks.With(document);
        foreach (var chunk in document.Chunks)
        {
            _vectors.Seed(new ChunkVector(
                document.TenantId, document.Id, chunk.Index, vectors[chunk.Index.Value], "test-model", null, chunk.SectionPath, document.AccessKeys));
        }

        return document;
    }

    private async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(AccessScope caller, int? topK = null)
    {
        var result = await Retriever().RetrieveAsync(caller, "How many days of leave?", topK);
        result.IsSuccess.ShouldBeTrue(result.ToString());
        return result.Value;
    }

    // ── Never outside the caller's scope ──────────────────────────────────────────

    [Fact]
    public async Task Identical_text_in_another_tenant_is_never_retrieved()
    {
        var mine = Index(TestData.Completed(chunkTexts: "Annual leave is 25 days."), Close);
        Index(TestData.Completed(TestData.OtherTenant, chunkTexts: "Annual leave is 25 days."), Same);

        var hits = await RetrieveAsync(TestData.Caller());

        hits.ShouldHaveSingleItem().Passage.DocumentId.ShouldBe(mine.Id);
    }

    [Fact]
    public async Task A_restricted_document_reaches_members_only()
    {
        var hr = Index(TestData.Completed(access: DocumentAccess.Of([AccessPrincipal.Group("hr")])), Same);

        (await RetrieveAsync(TestData.Caller(subject: "bob"))).ShouldBeEmpty();
        (await RetrieveAsync(TestData.Caller(subject: "carol", groups: "hr"))).ShouldHaveSingleItem().Passage.DocumentId.ShouldBe(hr.Id);
    }

    [Fact]
    public async Task SQL_decides_when_the_vector_payload_is_stale()
    {
        // Indexed tenant-wide; then access narrowed in SQL only, as if the payload update was lost.
        var document = Index(TestData.Completed(), Same);
        document.ChangeAccess(DocumentAccess.Of([AccessPrincipal.User("olivia")]), DateTimeOffset.UtcNow);

        (await RetrieveAsync(TestData.Caller(subject: "bob"))).ShouldBeEmpty();
        (await RetrieveAsync(TestData.Caller(subject: "olivia"))).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_document_that_is_not_completed_is_dropped_although_its_vectors_exist()
    {
        // Mid-embedding: chunks exist and some vectors are already in the store (Step 8 writes
        // them batch by batch), but "completed" is the promise that the document is whole.
        var document = TestData.Document();
        var at = DateTimeOffset.UtcNow;
        document.BeginExtraction(at);
        document.CompleteExtraction(at);
        document.AddChunks([new DocumentChunk(Guid.CreateVersion7(), document.Id, new ChunkIndex(0), "Half indexed.", new TokenCount(3), at)], at);
        Index(document, Same);

        (await RetrieveAsync(TestData.Caller())).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_orphan_vector_with_no_row_behind_it_is_dropped()
    {
        _vectors.Seed(new ChunkVector(TestData.Tenant, Guid.NewGuid(), new ChunkIndex(0), Same, "test-model", null, null, ["tenant"]));

        (await RetrieveAsync(TestData.Caller())).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_search_is_asked_with_the_callers_full_scope_and_the_question_model()
    {
        Index(TestData.Completed(), Same);

        await RetrieveAsync(TestData.Caller(subject: "carol", groups: "hr"));

        var filter = _vectors.SearchFilters.ShouldHaveSingleItem();
        filter.PinnedTenant.ShouldBe(TestData.Tenant);
        filter.Conditions.OfType<GrantedToAny>().Single().PrincipalKeys.ShouldBe(["tenant", "user:carol", "group:hr"]);
        filter.Conditions.OfType<EmbeddingModelIs>().Single().Model.ShouldBe(_embeddings.Model);
    }

    // ── Ranking and limits ────────────────────────────────────────────────────────

    [Fact]
    public async Task Chunks_come_back_best_first_and_below_the_floor_not_at_all()
    {
        var document = Index(TestData.Completed(chunkTexts: ["far", "close", "same"]), Far, Close, Same);

        var hits = await RetrieveAsync(TestData.Caller());

        // Far is cosine 0 = 0.5 normalized, under the 0.6 floor.
        hits.Select(hit => hit.Passage.Content).ShouldBe(["same", "close"]);
        hits[0].Score.Value.ShouldBe(1.0, tolerance: 1e-9);
        hits.ShouldAllBe(hit => hit.Passage.DocumentId == document.Id);
    }

    [Fact]
    public async Task Ties_are_broken_by_document_then_chunk_so_repeated_searches_agree()
    {
        var first = Index(TestData.Completed(chunkTexts: ["a1", "a0"]), Same, Same);
        var second = Index(TestData.Completed(chunkTexts: ["b0"]), Same);
        var hits = await RetrieveAsync(TestData.Caller());

        hits.Select(hit => (hit.Passage.DocumentId, hit.Passage.Index.Value)).ShouldBe(
            first.Id.CompareTo(second.Id) < 0
                ? [(first.Id, 0), (first.Id, 1), (second.Id, 0)]
                : [(second.Id, 0), (first.Id, 0), (first.Id, 1)]);
    }

    [Fact]
    public async Task Top_k_is_honoured_and_capped_at_the_configured_maximum()
    {
        Index(TestData.Completed(chunkTexts: [.. Enumerable.Range(0, 30).Select(i => $"chunk {i}")]), [.. Enumerable.Repeat(Same, 30)]);

        (await RetrieveAsync(TestData.Caller(), topK: 3)).Count.ShouldBe(3);
        (await RetrieveAsync(TestData.Caller())).Count.ShouldBe(_options.DefaultTopK);
        (await RetrieveAsync(TestData.Caller(), topK: 500)).Count.ShouldBe(_options.MaxTopK);
    }

    [Fact]
    public async Task Candidates_dropped_by_the_re_check_do_not_shrink_the_result_below_k()
    {
        // Two hidden chunks score higher than the visible ones. Over-fetching (2x) is what
        // still fills the caller's top 2.
        Index(TestData.Completed(access: DocumentAccess.Of([AccessPrincipal.User("olivia")]), chunkTexts: ["h0", "h1"]), Same, Same);
        Index(TestData.Completed(chunkTexts: ["v0", "v1"]), Close, Close);

        var hits = await RetrieveAsync(TestData.Caller(subject: "bob"), topK: 2);

        hits.Select(hit => hit.Passage.Content).ShouldBe(["v0", "v1"], ignoreOrder: true);
    }

    // ── Failure and emptiness ────────────────────────────────────────────────────

    [Fact]
    public async Task Nothing_relevant_is_a_successful_empty_result_not_an_error()
    {
        var result = await Retriever().RetrieveAsync(TestData.Caller(), "Anything at all?");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
        _chunks.Calls.ShouldBe(0); // no hits, no SQL round trip
    }

    [Fact]
    public async Task A_provider_failure_is_reported_as_itself_and_nothing_is_searched()
    {
        _embeddings.FailWith = AiErrors.Unavailable;

        var result = await Retriever().RetrieveAsync(TestData.Caller(), "leave?");

        result.Error.ShouldBe(AiErrors.Unavailable);
        _vectors.SearchFilters.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_blank_question_is_refused_without_calling_the_model()
    {
        (await Retriever().RetrieveAsync(TestData.Caller(), "   ")).Error.Code.ShouldBe("retrieval.empty_query");
        _embeddings.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task The_question_is_embedded_as_itself_trimmed()
    {
        await Retriever().RetrieveAsync(TestData.Caller(), "  How many days?  ");

        _embeddings.Inputs.ShouldBe(["How many days?"]);
    }

    [Fact]
    public async Task The_search_endpoint_reports_the_k_it_applied_and_rounds_scores()
    {
        Index(TestData.Completed(), Close);

        var result = await new SearchChunksHandler(Retriever()).HandleAsync(new SearchChunksQuery(TestData.Caller(), "leave?", 500));

        result.Value.TopK.ShouldBe(_options.MaxTopK);
        var hit = result.Value.Hits.ShouldHaveSingleItem();
        hit.Score.ShouldBe(Math.Round((Math.Sqrt(0.5) + 1) / 2, 4));
        hit.SectionPath.ShouldBe("Leave");
    }
}

public sealed class RetrievalValidationTests
{
    private static readonly SearchRequestValidator Validator = new(Options.Create(new RetrievalOptions { MaxQueryLength = 20 }));

    [Theory]
    [InlineData("", null, false)]
    [InlineData("   ", null, false)]
    [InlineData("leave?", 0, false)]
    [InlineData("leave?", -1, false)]
    [InlineData("this question is far too long", null, false)]
    [InlineData("leave?", null, true)]
    [InlineData("leave?", 1000, true)] // clamped later, not refused
    public void Search_requests_are_checked_for_shape(string query, int? topK, bool valid)
    {
        Validator.Validate(new SearchRequest(query, topK)).IsValid.ShouldBe(valid);
    }

    [Theory]
    [InlineData(0, 20, 0.6, 2, false)]
    [InlineData(21, 20, 0.6, 2, false)]
    [InlineData(5, 20, 1.5, 2, false)]
    [InlineData(5, 20, 0.6, 0, false)]
    [InlineData(5, 20, 0.6, 2, true)]
    public void Retrieval_options_are_validated_at_startup(int defaultTopK, int maxTopK, double minimumScore, int multiplier, bool valid)
    {
        var options = new RetrievalOptions { DefaultTopK = defaultTopK, MaxTopK = maxTopK, MinimumScore = minimumScore, CandidateMultiplier = multiplier };

        new RetrievalOptionsValidator().Validate(null, options).Succeeded.ShouldBe(valid);
    }
}
