using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using Shouldly;

namespace EKA.Domain.Tests;

public sealed class DocumentTests
{
    // ── Construction ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_new_document_starts_pending_and_unsearchable()
    {
        var document = TestData.NewDocument();

        document.Status.ShouldBe(IngestionStatus.Pending);
        document.IsSearchable.ShouldBeFalse();
        document.ChunkCount.ShouldBe(0);
        document.Chunks.ShouldBeEmpty();
        document.FailureCount.ShouldBe(0);
        document.IngestedAt.ShouldBeNull();
    }

    [Fact]
    public void Authority_defaults_to_standard_when_not_specified()
    {
        TestData.NewDocument().Authority.ShouldBe(AuthorityScore.Standard);
    }

    [Fact]
    public void A_document_must_belong_to_a_tenant()
    {
        // default(TenantId) bypasses TenantId's own validation because C# gives every
        // struct an implicit parameterless constructor. The aggregate re-checks.
        var act = () => new Document(
            Guid.NewGuid(),
            default,
            TestData.Title(),
            TestData.Pdf(),
            "key",
            TestData.Now);

        Should.Throw<DomainException>(act).Message.ShouldContain("must belong to a tenant");
    }

    [Fact]
    public void A_document_requires_a_non_default_id()
    {
        var act = () => new Document(
            Guid.Empty,
            TestData.Tenant,
            TestData.Title(),
            TestData.Pdf(),
            "key",
            TestData.Now);

        Should.Throw<DomainException>(act).Message.ShouldContain("non-default id");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_document_must_record_where_its_file_is_stored(string storageKey)
    {
        var act = () => new Document(
            Guid.NewGuid(),
            TestData.Tenant,
            TestData.Title(),
            TestData.Pdf(),
            storageKey,
            TestData.Now);

        Should.Throw<DomainException>(act);
    }

    // ── The happy path ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_full_pipeline_walks_pending_to_completed()
    {
        var document = TestData.NewDocument();

        document.BeginExtraction(TestData.Now);
        document.Status.ShouldBe(IngestionStatus.Extracting);

        document.CompleteExtraction(TestData.Now);
        document.Status.ShouldBe(IngestionStatus.Chunking);
        document.ExtractedAt.ShouldBe(TestData.Now);

        document.AddChunks(TestData.Chunks(document.Id, 4), TestData.Now);
        document.Status.ShouldBe(IngestionStatus.Embedding);
        document.ChunkCount.ShouldBe(4);

        document.CompleteIngestion(TestData.Later);
        document.Status.ShouldBe(IngestionStatus.Completed);
        document.IsSearchable.ShouldBeTrue();
        document.IngestedAt.ShouldBe(TestData.Later);
    }

    // ── Illegal transitions ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(IngestionStatus.Extracting)]
    [InlineData(IngestionStatus.Chunking)]
    [InlineData(IngestionStatus.Embedding)]
    [InlineData(IngestionStatus.Completed)]
    [InlineData(IngestionStatus.Failed)]
    public void Extraction_can_only_begin_from_pending(IngestionStatus status)
    {
        var document = TestData.DocumentIn(status);

        Should.Throw<InvalidStateTransitionException>(
            () => document.BeginExtraction(TestData.Now));
    }

    [Theory]
    [InlineData(IngestionStatus.Pending)]
    [InlineData(IngestionStatus.Chunking)]
    [InlineData(IngestionStatus.Embedding)]
    [InlineData(IngestionStatus.Completed)]
    [InlineData(IngestionStatus.Failed)]
    public void Extraction_can_only_complete_from_extracting(IngestionStatus status)
    {
        var document = TestData.DocumentIn(status);

        Should.Throw<InvalidStateTransitionException>(
            () => document.CompleteExtraction(TestData.Now));
    }

    [Theory]
    [InlineData(IngestionStatus.Pending)]
    [InlineData(IngestionStatus.Extracting)]
    [InlineData(IngestionStatus.Embedding)]
    [InlineData(IngestionStatus.Completed)]
    [InlineData(IngestionStatus.Failed)]
    public void Chunks_can_only_be_added_while_chunking(IngestionStatus status)
    {
        var document = TestData.DocumentIn(status);

        Should.Throw<InvalidStateTransitionException>(
            () => document.AddChunks(TestData.Chunks(document.Id, 2), TestData.Now));
    }

    [Theory]
    [InlineData(IngestionStatus.Pending)]
    [InlineData(IngestionStatus.Extracting)]
    [InlineData(IngestionStatus.Chunking)]
    [InlineData(IngestionStatus.Completed)]
    [InlineData(IngestionStatus.Failed)]
    public void Ingestion_can_only_complete_from_embedding(IngestionStatus status)
    {
        var document = TestData.DocumentIn(status);

        Should.Throw<InvalidStateTransitionException>(
            () => document.CompleteIngestion(TestData.Now));
    }

    [Fact]
    public void The_transition_exception_names_both_states()
    {
        var document = TestData.DocumentIn(IngestionStatus.Completed);

        var exception = Should.Throw<InvalidStateTransitionException>(
            () => document.BeginExtraction(TestData.Now));

        exception.Entity.ShouldBe(nameof(Document));
        exception.From.ShouldBe(nameof(IngestionStatus.Completed));
        exception.To.ShouldBe(nameof(IngestionStatus.Extracting));
    }

    // ── Chunk invariants ────────────────────────────────────────────────────────────

    [Fact]
    public void A_document_that_produced_no_chunks_cannot_proceed()
    {
        var document = TestData.DocumentIn(IngestionStatus.Chunking);

        var act = () => document.AddChunks([], TestData.Now);

        Should.Throw<DomainException>(act).Message.ShouldContain("no chunks");
    }

    [Fact]
    public void Chunks_belonging_to_another_document_are_rejected()
    {
        var document = TestData.DocumentIn(IngestionStatus.Chunking);
        var foreign = TestData.Chunks(Guid.NewGuid(), 2);

        var act = () => document.AddChunks(foreign, TestData.Now);

        Should.Throw<DomainException>(act).Message.ShouldContain("belongs to document");
    }

    [Fact]
    public void A_gap_in_chunk_indexes_is_rejected()
    {
        // A missing index means the chunker silently dropped part of the document. The
        // symptom would otherwise surface much later as an answer confidently missing a
        // paragraph — nearly impossible to trace backwards from the output.
        var document = TestData.DocumentIn(IngestionStatus.Chunking);
        List<DocumentChunk> withGap =
        [
            TestData.Chunk(document.Id, 0),
            TestData.Chunk(document.Id, 1),
            TestData.Chunk(document.Id, 3),
        ];

        var act = () => document.AddChunks(withGap, TestData.Now);

        Should.Throw<DomainException>(act).Message.ShouldContain("contiguous");
    }

    [Fact]
    public void Duplicate_chunk_indexes_are_rejected()
    {
        var document = TestData.DocumentIn(IngestionStatus.Chunking);
        List<DocumentChunk> duplicated =
        [
            TestData.Chunk(document.Id, 0),
            TestData.Chunk(document.Id, 1),
            TestData.Chunk(document.Id, 1),
        ];

        Should.Throw<DomainException>(() => document.AddChunks(duplicated, TestData.Now));
    }

    [Fact]
    public void Chunks_must_start_at_zero()
    {
        var document = TestData.DocumentIn(IngestionStatus.Chunking);
        List<DocumentChunk> oneBased =
        [
            TestData.Chunk(document.Id, 1),
            TestData.Chunk(document.Id, 2),
        ];

        Should.Throw<DomainException>(() => document.AddChunks(oneBased, TestData.Now));
    }

    [Fact]
    public void Chunks_are_stored_in_index_order_regardless_of_input_order()
    {
        var document = TestData.DocumentIn(IngestionStatus.Chunking);
        List<DocumentChunk> shuffled =
        [
            TestData.Chunk(document.Id, 2),
            TestData.Chunk(document.Id, 0),
            TestData.Chunk(document.Id, 1),
        ];

        document.AddChunks(shuffled, TestData.Now);

        document.Chunks.Select(c => c.Index.Value).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public void The_chunk_collection_cannot_be_mutated_behind_the_aggregate()
    {
        var document = TestData.DocumentIn(IngestionStatus.Embedding);

        // Worth knowing precisely: List<T>.AsReadOnly() returns ReadOnlyCollection<T>,
        // which DOES implement ICollection<T> (explicitly), so this cast compiles and
        // succeeds. What it does not do is permit mutation — every write member throws.
        // Encapsulation here is enforced at runtime, not made impossible by the type
        // system, which matters if you were planning to rely on IReadOnlyCollection<T>
        // as a security boundary rather than as a design signal.
        var asMutable = (ICollection<DocumentChunk>)document.Chunks;

        Should.Throw<NotSupportedException>(
            () => asMutable.Add(TestData.Chunk(document.Id, 99)));
        Should.Throw<NotSupportedException>(asMutable.Clear);

        document.Chunks.Count.ShouldBe(3);
    }

    // ── Events ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Completing_ingestion_raises_an_ingested_event()
    {
        var document = TestData.DocumentIn(IngestionStatus.Embedding);

        document.CompleteIngestion(TestData.Later);

        var raised = document.DomainEvents.OfType<DocumentIngestedEvent>().ShouldHaveSingleItem();
        raised.DocumentId.ShouldBe(document.Id);
        raised.TenantId.ShouldBe(TestData.Tenant);
        raised.ChunkCount.ShouldBe(3);
        raised.OccurredAt.ShouldBe(TestData.Later);
    }

    [Fact]
    public void Clearing_events_empties_the_collection()
    {
        var document = TestData.DocumentIn(IngestionStatus.Embedding);
        document.CompleteIngestion(TestData.Later);

        document.ClearDomainEvents();

        document.DomainEvents.ShouldBeEmpty();
    }

    // ── Failure and retry ───────────────────────────────────────────────────────────

    [Fact]
    public void Failing_records_the_reason_the_stage_and_raises_an_event()
    {
        var document = TestData.DocumentIn(IngestionStatus.Extracting);

        document.Fail("PDF is password protected", TestData.Later);

        document.Status.ShouldBe(IngestionStatus.Failed);
        document.FailureReason.ShouldBe("PDF is password protected");
        document.FailureCount.ShouldBe(1);

        var raised = document.DomainEvents
            .OfType<DocumentIngestionFailedEvent>()
            .ShouldHaveSingleItem();
        raised.FailedAt.ShouldBe(IngestionStatus.Extracting);
        raised.Reason.ShouldBe("PDF is password protected");
        raised.FailureCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_failure_must_record_why(string reason)
    {
        var document = TestData.DocumentIn(IngestionStatus.Extracting);

        Should.Throw<DomainException>(() => document.Fail(reason, TestData.Now));
    }

    [Fact]
    public void A_completed_document_cannot_be_failed()
    {
        var document = TestData.DocumentIn(IngestionStatus.Completed);

        Should.Throw<InvalidStateTransitionException>(
            () => document.Fail("too late", TestData.Now));
    }

    [Fact]
    public void Reset_returns_a_failed_document_to_pending_and_discards_its_chunks()
    {
        // This is what makes re-ingestion idempotent. Without discarding chunks, a retry
        // appends a second full set and every query returns duplicate passages.
        var document = TestData.DocumentIn(IngestionStatus.Failed);
        document.ChunkCount.ShouldBe(3);

        document.Reset(TestData.Later);

        document.Status.ShouldBe(IngestionStatus.Pending);
        document.Chunks.ShouldBeEmpty();
        document.ChunkCount.ShouldBe(0);
        document.ExtractedAt.ShouldBeNull();
        document.IngestedAt.ShouldBeNull();
    }

    [Fact]
    public void Reset_preserves_the_failure_count_so_repeated_failures_can_be_escalated()
    {
        var document = TestData.DocumentIn(IngestionStatus.Failed);

        document.Reset(TestData.Later);
        document.BeginExtraction(TestData.Later);
        document.Fail("failed again", TestData.Later);

        document.FailureCount.ShouldBe(2);
    }

    [Theory]
    [InlineData(IngestionStatus.Pending)]
    [InlineData(IngestionStatus.Extracting)]
    [InlineData(IngestionStatus.Chunking)]
    [InlineData(IngestionStatus.Embedding)]
    [InlineData(IngestionStatus.Completed)]
    public void Only_a_failed_document_can_be_reset(IngestionStatus status)
    {
        var document = TestData.DocumentIn(status);

        Should.Throw<InvalidStateTransitionException>(() => document.Reset(TestData.Now));
    }

    [Fact]
    public void A_document_can_be_ingested_again_after_a_reset()
    {
        var document = TestData.DocumentIn(IngestionStatus.Failed);

        document.Reset(TestData.Later);
        document.BeginExtraction(TestData.Later);
        document.CompleteExtraction(TestData.Later);
        document.AddChunks(TestData.Chunks(document.Id, 2), TestData.Later);
        document.CompleteIngestion(TestData.Later);

        document.IsSearchable.ShouldBeTrue();
        document.ChunkCount.ShouldBe(2);
    }

    // ── Metadata ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renaming_updates_the_title_and_the_modified_timestamp()
    {
        var document = TestData.NewDocument();

        document.Rename(new DocumentTitle("Employee Handbook 2027"), TestData.Later);

        document.Title.Value.ShouldBe("Employee Handbook 2027");
        document.ModifiedAt.ShouldBe(TestData.Later);
    }

    [Fact]
    public void Authority_can_be_raised_after_review()
    {
        var document = TestData.NewDocument();

        document.SetAuthority(AuthorityScore.Authoritative, TestData.Later);

        document.Authority.Value.ShouldBe(100);
    }

    [Fact]
    public void Metadata_changes_are_legal_in_any_state()
    {
        var document = TestData.DocumentIn(IngestionStatus.Completed);

        Should.NotThrow(() => document.Rename(new DocumentTitle("Renamed"), TestData.Later));
        Should.NotThrow(() => document.SetAuthority(AuthorityScore.Unverified, TestData.Later));
    }
}
