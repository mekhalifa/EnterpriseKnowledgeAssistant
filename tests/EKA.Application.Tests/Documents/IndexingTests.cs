using System.Text;
using EKA.Application.Abstractions.AI;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Documents.DeleteDocument;
using EKA.Application.Documents.IndexDocument;
using EKA.Application.Documents.Indexing;
using EKA.Application.Documents.Ingestion;
using EKA.Application.Tests.Fakes;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace EKA.Application.Tests.Documents;

/// <summary>
/// The chunking strategy. Small numbers on purpose: a 50-token target is 200 characters with
/// the estimating counter, so every boundary in these tests can be worked out by hand.
/// </summary>
public sealed class SectionAwareChunkerTests
{
    private const int Target = 50;

    private static SectionAwareChunker Chunker(int target = Target, int overlap = 20) =>
        new(new EstimatingTokenCounter(), Options.Create(new IndexingOptions { ChunkTargetTokens = target, ChunkOverlapTokens = overlap }));

    /// <summary>Exactly 60 characters, so 15 tokens alone and 16 after a paragraph break.</summary>
    private static string Paragraph(int i) => $"P{i:00}" + new string('a', 57);

    private static string Paragraphs(int count) =>
        string.Join("\n\n", Enumerable.Range(0, count).Select(Paragraph));

    [Fact]
    public void A_short_section_is_one_chunk_that_keeps_its_page_and_heading()
    {
        var chunks = Chunker().Chunk([new ExtractedSection(3, "Benefits / Annual leave", "Employees receive 25 days.")]);

        var chunk = chunks.ShouldHaveSingleItem();
        chunk.Content.ShouldBe("Employees receive 25 days.");
        chunk.Pages.ShouldBe(PageRange.SinglePage(3));
        chunk.SectionPath.ShouldBe("Benefits / Annual leave");
        chunk.Tokens.ShouldBe(new TokenCount(7));
    }

    [Fact]
    public void Text_under_different_headings_is_never_merged_into_one_chunk()
    {
        // Both would fit in one chunk easily. Merging them would cite one heading for text
        // that sits under another.
        var chunks = Chunker().Chunk(
        [
            new ExtractedSection(null, "Benefits / Annual leave", "25 days."),
            new ExtractedSection(null, "Benefits / Sick leave", "10 days."),
        ]);

        chunks.Select(c => c.SectionPath).ShouldBe(["Benefits / Annual leave", "Benefits / Sick leave"]);
    }

    [Fact]
    public void Small_neighbouring_pages_merge_and_the_chunk_cites_the_page_range()
    {
        var chunks = Chunker().Chunk(
        [
            new ExtractedSection(3, null, "End of a chapter."),
            new ExtractedSection(4, null, "A short page."),
        ]);

        var chunk = chunks.ShouldHaveSingleItem();
        chunk.Content.ShouldBe("End of a chapter.\n\nA short page.");
        chunk.Pages.ShouldBe(new PageRange(3, 4));
    }

    [Fact]
    public void Pages_stop_merging_once_the_next_would_overflow_the_target()
    {
        var chunks = Chunker().Chunk(
        [
            new ExtractedSection(1, null, Paragraph(1)),
            new ExtractedSection(2, null, Paragraph(2)),
            new ExtractedSection(3, null, Paragraph(3)),
            new ExtractedSection(4, null, Paragraph(4)),
        ]);

        // 15 + 16 + 16 = 47 tokens fit; a fourth page would make 63.
        chunks.Select(c => c.Pages).ShouldBe([new PageRange(1, 3), PageRange.SinglePage(4)]);
    }

    [Fact]
    public void A_long_section_becomes_overlapping_windows_that_break_between_paragraphs()
    {
        var chunks = Chunker().Chunk([new ExtractedSection(1, "Handbook", Paragraphs(10))]);

        // Three paragraphs per window (47 tokens); each window repeats the previous window's
        // last paragraph (16 tokens, within the 20-token overlap).
        chunks.Select(c => c.Content[..3]).ShouldBe(["P00", "P02", "P04", "P06", "P08"]);
        chunks[0].Content.ShouldBe(string.Join("\n\n", Paragraph(0), Paragraph(1), Paragraph(2)));
        chunks[^1].Content.ShouldBe(string.Join("\n\n", Paragraph(8), Paragraph(9)));
        chunks.ShouldAllBe(c => c.SectionPath == "Handbook" && c.Pages == PageRange.SinglePage(1));
    }

    [Fact]
    public void Without_overlap_no_text_is_repeated_and_none_is_lost()
    {
        var source = Paragraphs(10);

        var chunks = Chunker(overlap: 0).Chunk([new ExtractedSection(null, null, source)]);

        string.Join("\n\n", chunks.Select(c => c.Content)).ShouldBe(source);
    }

    [Fact]
    public void No_chunk_exceeds_the_target_even_for_a_sentence_with_no_punctuation()
    {
        var wall = string.Join(' ', Enumerable.Range(0, 400).Select(i => $"w{i}"));
        var unbroken = new string('x', 1000);

        var chunks = Chunker().Chunk(
        [
            new ExtractedSection(null, "Wall", wall),
            new ExtractedSection(null, "Unbroken", unbroken),
        ]);

        chunks.Count.ShouldBeGreaterThan(4);
        chunks.ShouldAllBe(c => c.Tokens.Value <= Target);

        // Nothing lost: every word of the wall is in some chunk, in order.
        var words = chunks.Where(c => c.SectionPath == "Wall").SelectMany(c => c.Content.Split(' ')).Distinct().ToList();
        words.ShouldBe(Enumerable.Range(0, 400).Select(i => $"w{i}").ToList());
        string.Concat(chunks.Where(c => c.SectionPath == "Unbroken").Select(c => c.Content)).ShouldBe(unbroken);
    }

    [Fact]
    public void Long_paragraphs_split_at_sentence_ends()
    {
        var sentences = Enumerable.Range(0, 12).Select(i => $"Sentence {i:00} says something useful here.").ToList();

        var chunks = Chunker(overlap: 0).Chunk([new ExtractedSection(null, null, string.Join(' ', sentences))]);

        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(c => c.Content.StartsWith("Sentence ") && c.Content.EndsWith('.'));
    }

    [Fact]
    public void Blank_sections_produce_no_chunks()
    {
        Chunker().Chunk([new ExtractedSection(1, null, "  \n\n "), new ExtractedSection(2, null, "")]).ShouldBeEmpty();
    }

    [Fact]
    public void The_same_input_always_produces_the_same_chunks()
    {
        ExtractedSection[] sections = [new(1, "A", Paragraphs(7)), new(2, "B", "Short.")];

        Chunker().Chunk(sections).ShouldBe(Chunker().Chunk(sections));
    }

    [Theory]
    [InlineData(49, 10, 64, 2)]
    [InlineData(500, 251, 64, 2)]
    [InlineData(500, 75, 0, 2)]
    [InlineData(500, 75, 64, 0)]
    public void Indexing_options_outside_their_bounds_are_refused_at_startup(int target, int overlap, int batch, int concurrency)
    {
        var options = new IndexingOptions
        {
            ChunkTargetTokens = target,
            ChunkOverlapTokens = overlap,
            EmbeddingBatchSize = batch,
            MaxConcurrentBatches = concurrency,
        };

        // Resolved the way the host resolves it, so the test also proves it is registered.
        var registration = new ServiceCollection().AddApplication()
            .Single(d => d.ServiceType == typeof(IValidateOptions<IndexingOptions>));
        var validator = (IValidateOptions<IndexingOptions>)Activator.CreateInstance(registration.ImplementationType!, nonPublic: true)!;

        validator.Validate(null, options).Failed.ShouldBeTrue();
        validator.Validate(null, new IndexingOptions()).Succeeded.ShouldBeTrue();
    }
}

public sealed class IndexDocumentHandlerTests
{
    private readonly FakeDocumentRepository _documents = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeFileStorage _storage = new();
    private readonly FakeEmbeddingService _embeddings = new();
    private readonly FakeVectorStore _vectors = new();

    private IndexDocumentHandler Handler() => new(
        _documents,
        _unitOfWork,
        new ExtractedTextStore(_storage),
        new SectionAwareChunker(new EstimatingTokenCounter(), Options.Create(new IndexingOptions { ChunkTargetTokens = 50, ChunkOverlapTokens = 10 })),
        _embeddings,
        _vectors,
        Options.Create(new IndexingOptions { EmbeddingBatchSize = 2, MaxConcurrentBatches = 2 }),
        FixedTimeProvider.Default,
        NullLogger<IndexDocumentHandler>.Instance);

    /// <summary>A document in Chunking with its artifact stored: one short section per heading, so one chunk each.</summary>
    private async Task<Document> ExtractedDocument(int sections = 5)
    {
        var document = TestData.Document();
        document.BeginExtraction(FixedTimeProvider.DefaultNow);
        document.CompleteExtraction(FixedTimeProvider.DefaultNow);
        _documents.With(document);

        var extracted = new ExtractedDocument([.. Enumerable.Range(0, sections).Select(i => new ExtractedSection(i + 1, $"Policy / Part {i}", $"Text of part {i}."))]);
        await new ExtractedTextStore(_storage).SaveAsync(document, extracted, FixedTimeProvider.DefaultNow);

        return document;
    }

    private static IndexDocumentCommand For(Document document) => new(document.TenantId, document.Id);

    // ── The exit criterion ─────────────────────────────────────────────────────────

    [Fact]
    public async Task An_extracted_document_ends_completed_with_ordered_chunks_and_one_vector_each()
    {
        var document = await ExtractedDocument(sections: 5);

        var outcome = await Handler().HandleAsync(For(document));

        outcome.ShouldBe(IndexingOutcome.Indexed);
        document.Status.ShouldBe(IngestionStatus.Completed);
        document.Chunks.OrderBy(c => c.Index).Select(c => c.Content).ShouldBe(Enumerable.Range(0, 5).Select(i => $"Text of part {i}.").ToList());
        document.Chunks.ShouldAllBe(c => c.EmbeddingModel == "fake-embedding-model");

        _vectors.Points.Count.ShouldBe(5);
        _vectors.Points.Select(p => p.ChunkIndex.Value).ShouldBe([0, 1, 2, 3, 4]);
        _vectors.Points.ShouldAllBe(p => p.TenantId == document.TenantId && p.DocumentId == document.Id);
        _vectors.Points[2].SectionPath.ShouldBe("Policy / Part 2");
        _vectors.Points[2].Pages.ShouldBe(PageRange.SinglePage(3));
    }

    [Fact]
    public async Task The_text_embedded_carries_the_title_and_heading_but_the_stored_chunk_does_not()
    {
        var document = await ExtractedDocument(sections: 1);

        await Handler().HandleAsync(For(document));

        _embeddings.Batches.Single().Single().ShouldBe("Employee handbook\nPolicy / Part 0\n\nText of part 0.");
        document.Chunks.Single().Content.ShouldBe("Text of part 0.");
    }

    [Fact]
    public async Task Batches_are_bounded_and_no_more_than_the_limit_are_ever_in_flight()
    {
        var document = await ExtractedDocument(sections: 9);
        _embeddings.Latency = TimeSpan.FromMilliseconds(50);

        await Handler().HandleAsync(For(document));

        _embeddings.Batches.Select(b => b.Count).ShouldBe([2, 2, 2, 2, 1]);
        _embeddings.MaxInFlight.ShouldBe(2);
    }

    // ── Safe to repeat ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Vectors_left_by_an_earlier_attempt_are_removed_before_new_chunks_are_written()
    {
        var document = await ExtractedDocument(sections: 2);

        // A reset document once had more chunks. Index 7 does not exist any more, and a
        // deterministic id alone would never overwrite it.
        _vectors.Seed(new ChunkVector(document.TenantId, document.Id, new ChunkIndex(7), new float[4], "old-model", null, null, ["tenant"]));

        await Handler().HandleAsync(For(document));

        _vectors.Points.Select(p => p.ChunkIndex.Value).ShouldBe([0, 1]);
    }

    [Fact]
    public async Task A_redelivered_job_for_a_completed_document_does_nothing()
    {
        var document = await ExtractedDocument();
        await Handler().HandleAsync(For(document));
        var calls = _embeddings.Batches.Count;

        var outcome = await Handler().HandleAsync(For(document));

        outcome.ShouldBe(IndexingOutcome.NothingOwed);
        _embeddings.Batches.Count.ShouldBe(calls);
        _vectors.Points.Count.ShouldBe(5);
    }

    [Fact]
    public async Task Another_tenants_command_finds_nothing_to_index()
    {
        var document = await ExtractedDocument();

        var outcome = await Handler().HandleAsync(new IndexDocumentCommand(TestData.OtherTenant, document.Id));

        outcome.ShouldBe(IndexingOutcome.NothingOwed);
        document.Status.ShouldBe(IngestionStatus.Chunking);
    }

    // ── Failures: transient keeps progress, permanent fails the document ───────────

    [Fact]
    public async Task A_transient_provider_failure_keeps_the_finished_batches_and_the_retry_does_only_the_rest()
    {
        var document = await ExtractedDocument(sections: 5);
        _embeddings.FailOnCall = (2, AiErrors.RateLimited);

        var thrown = await Should.ThrowAsync<EmbeddingUnavailableException>(() => Handler().HandleAsync(For(document)));

        thrown.Error.ShouldBe(AiErrors.RateLimited);
        document.Status.ShouldBe(IngestionStatus.Embedding);
        document.Chunks.Where(c => c.IsEmbedded).Select(c => c.Index.Value).ShouldBe([0, 1]);

        // The job engine retries. Only chunks 2, 3 and 4 are embedded this time.
        var outcome = await Handler().HandleAsync(For(document));

        outcome.ShouldBe(IndexingOutcome.Indexed);
        _embeddings.Batches.Skip(2).SelectMany(b => b).ShouldAllBe(text => !text.Contains("part 0") && !text.Contains("part 1"));
        _vectors.Points.Count.ShouldBe(5);
    }

    [Fact]
    public async Task A_vector_store_failure_is_left_to_the_retry_and_the_sibling_batch_is_kept()
    {
        var document = await ExtractedDocument(sections: 4);
        _vectors.FailOnUpsertCall = 2;

        await Should.ThrowAsync<IOException>(() => Handler().HandleAsync(For(document)));

        document.Status.ShouldBe(IngestionStatus.Embedding);
        document.Chunks.Count(c => c.IsEmbedded).ShouldBe(2);
        _unitOfWork.SaveCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_provider_refusal_fails_the_document_and_leaves_no_vectors_behind()
    {
        var document = await ExtractedDocument(sections: 5);
        _embeddings.FailOnCall = (3, AiErrors.Rejected);

        var outcome = await Handler().HandleAsync(For(document));

        outcome.ShouldBe(IndexingOutcome.Failed);
        document.Status.ShouldBe(IngestionStatus.Failed);
        document.FailureReason.ShouldNotBeNull().ShouldContain("refused");
        _vectors.Points.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_artifact_fails_the_document_with_a_way_out()
    {
        var document = await ExtractedDocument();
        await _storage.DeleteAsync(ExtractedTextStore.KeyFor(document));

        var outcome = await Handler().HandleAsync(For(document));

        outcome.ShouldBe(IndexingOutcome.Failed);
        document.FailureReason.ShouldNotBeNull().ShouldContain("reset the document");
    }

    [Theory]
    [InlineData(2, null)]
    [InlineData(1, "0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task An_artifact_from_another_format_or_other_bytes_is_never_indexed(int formatVersion, string? contentHash)
    {
        var document = await ExtractedDocument();
        var json = $$"""
            {"formatVersion":{{formatVersion}},"documentId":"{{document.Id}}","contentHash":"{{contentHash ?? document.File.ContentHash}}",
             "extractedAt":"2026-01-01T00:00:00+00:00","sections":[{"pageNumber":1,"sectionPath":null,"text":"Stale text."}]}
            """;
        _storage.Put(ExtractedTextStore.KeyFor(document), Encoding.UTF8.GetBytes(json));

        var outcome = await Handler().HandleAsync(For(document));

        outcome.ShouldBe(IndexingOutcome.Failed);
        _embeddings.Batches.ShouldBeEmpty();
    }
}

public sealed class DeleteDocumentVectorTests
{
    [Fact]
    public async Task Deleting_a_document_removes_its_vectors_and_no_one_elses()
    {
        var documents = new FakeDocumentRepository();
        var vectors = new FakeVectorStore();
        var document = TestData.Document();
        var neighbour = TestData.Document(contentHash: TestData.HashOf('c'));
        documents.With(document).With(neighbour);
        vectors.Seed(new ChunkVector(document.TenantId, document.Id, new ChunkIndex(0), new float[4], "m", null, null, ["tenant"]));
        vectors.Seed(new ChunkVector(neighbour.TenantId, neighbour.Id, new ChunkIndex(0), new float[4], "m", null, null, ["tenant"]));

        await new DeleteDocumentHandler(documents, new FakeUnitOfWork(), new FakeFileStorage(), vectors, NullLogger<DeleteDocumentHandler>.Instance)
            .HandleAsync(new DeleteDocumentCommand(TestData.Caller(document.TenantId), document.Id));

        vectors.Points.ShouldHaveSingleItem().DocumentId.ShouldBe(neighbour.Id);
    }

    [Fact]
    public async Task A_vector_store_outage_does_not_undo_or_fail_a_committed_delete()
    {
        var documents = new FakeDocumentRepository();
        var unitOfWork = new FakeUnitOfWork();
        var document = TestData.Document();
        documents.With(document);

        var result = await new DeleteDocumentHandler(documents, unitOfWork, new FakeFileStorage(), new UnreachableVectorStore(), NullLogger<DeleteDocumentHandler>.Instance)
            .HandleAsync(new DeleteDocumentCommand(TestData.Caller(document.TenantId), document.Id));

        result.IsSuccess.ShouldBeTrue();
        documents.Removed.ShouldContain(document);
        unitOfWork.SaveCount.ShouldBe(1);
    }

    private sealed class UnreachableVectorStore : IVectorStore
    {
        public Task UpsertAsync(IReadOnlyList<ChunkVector> vectors, CancellationToken cancellationToken = default) =>
            throw new IOException("unreachable");

        public Task DeleteDocumentAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default) =>
            throw new IOException("unreachable");

        public Task<long> CountDocumentAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default) =>
            throw new IOException("unreachable");

        public Task<IReadOnlyList<VectorHit>> SearchAsync(ReadOnlyMemory<float> query, EKA.Application.Retrieval.ChunkFilter filter, int limit, EKA.Domain.Retrieval.SimilarityScore minimumScore, CancellationToken cancellationToken = default) =>
            throw new IOException("unreachable");

        public Task SetDocumentAccessAsync(TenantId tenantId, Guid documentId, IReadOnlyList<string> accessKeys, CancellationToken cancellationToken = default) =>
            throw new IOException("unreachable");
    }
}
