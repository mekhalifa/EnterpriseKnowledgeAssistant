using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using Shouldly;

namespace EKA.Domain.Tests;

public sealed class DocumentChunkTests
{
    // ── The P0 regression ───────────────────────────────────────────────────────────
    // The original codebase validated Content before assigning it, so the guard tested a
    // null the caller never passed and EVERY chunk failed construction. This test is the
    // one that would have caught it: a perfectly valid chunk must simply be creatable.

    [Fact]
    public void A_valid_chunk_is_constructed_successfully()
    {
        var documentId = Guid.NewGuid();

        var chunk = TestData.Chunk(documentId, index: 0, content: "The leave policy is...");

        chunk.DocumentId.ShouldBe(documentId);
        chunk.Index.Value.ShouldBe(0);
        chunk.Content.ShouldBe("The leave policy is...");
        chunk.CreatedAt.ShouldBe(TestData.Now);
        chunk.IsEmbedded.ShouldBeFalse();
    }

    [Fact]
    public void Id_comes_from_the_base_class_and_is_not_shadowed()
    {
        // README P1: a derived Id would shadow EntityAudit<Guid>.Id, leaving two
        // properties where persistence maps one and the code reads the other.
        var id = Guid.NewGuid();

        var chunk = new DocumentChunk(
            id,
            Guid.NewGuid(),
            ChunkIndex.First,
            "text",
            TokenCount.Zero,
            TestData.Now);

        chunk.Id.ShouldBe(id);
        typeof(DocumentChunk).GetProperty(nameof(DocumentChunk.Id))!
            .DeclaringType.ShouldBe(typeof(EntityAudit<Guid>));
    }

    // ── Guards ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_chunk_must_belong_to_a_document()
    {
        var act = () => TestData.Chunk(Guid.Empty, index: 0);

        Should.Throw<DomainException>(act)
            .Message.ShouldContain("must belong to a document");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_chunk_cannot_have_empty_content(string content)
    {
        var act = () => TestData.Chunk(Guid.NewGuid(), index: 0, content: content);

        Should.Throw<DomainException>(act).Message.ShouldContain("no content");
    }

    [Fact]
    public void A_blank_section_path_is_rejected_but_null_is_allowed()
    {
        var documentId = Guid.NewGuid();

        Should.Throw<DomainException>(() => new DocumentChunk(
            Guid.NewGuid(),
            documentId,
            ChunkIndex.First,
            "text",
            TokenCount.Zero,
            TestData.Now,
            sectionPath: "   "));

        Should.NotThrow(() => new DocumentChunk(
            Guid.NewGuid(),
            documentId,
            ChunkIndex.First,
            "text",
            TokenCount.Zero,
            TestData.Now,
            sectionPath: null));
    }

    [Fact]
    public void Content_whitespace_is_preserved_because_overlap_depends_on_it()
    {
        var chunk = TestData.Chunk(Guid.NewGuid(), 0, content: "  leading and trailing  ");

        chunk.Content.ShouldBe("  leading and trailing  ");
    }

    [Fact]
    public void Section_path_is_trimmed()
    {
        var chunk = new DocumentChunk(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ChunkIndex.First,
            "text",
            TokenCount.Zero,
            TestData.Now,
            sectionPath: "  Benefits / Leave  ");

        chunk.SectionPath.ShouldBe("Benefits / Leave");
    }

    // ── Embedding ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Marking_a_chunk_embedded_records_the_model_and_time()
    {
        var chunk = TestData.Chunk(Guid.NewGuid(), 0);

        chunk.MarkEmbedded("text-embedding-3-large", TestData.Later);

        chunk.IsEmbedded.ShouldBeTrue();
        chunk.EmbeddingModel.ShouldBe("text-embedding-3-large");
        chunk.EmbeddedAt.ShouldBe(TestData.Later);
        chunk.ModifiedAt.ShouldBe(TestData.Later);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Embedding_requires_a_model_name(string model)
    {
        var chunk = TestData.Chunk(Guid.NewGuid(), 0);

        // Without the model name there is no way to know later whether a stored vector is
        // still comparable to a query vector.
        Should.Throw<DomainException>(() => chunk.MarkEmbedded(model, TestData.Later));
    }
}
