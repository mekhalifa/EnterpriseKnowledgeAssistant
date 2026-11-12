using EKA.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EKA.Infrastructure.Persistence.Configurations;

internal sealed class DocumentChunkConfiguration : IEntityTypeConfiguration<DocumentChunk>
{
    public void Configure(EntityTypeBuilder<DocumentChunk> builder)
    {
        builder.ToTable("DocumentChunks");

        builder.HasKey(chunk => chunk.Id);

        builder.Property(chunk => chunk.DocumentId)
            .IsRequired();

        builder.Property(chunk => chunk.Index)
            .HasColumnName("ChunkIndex")
            .IsRequired();

        // Deliberately nvarchar(max): a chunk is prose of unpredictable length, and a
        // truncating column would silently corrupt the text that answers get grounded in.
        builder.Property(chunk => chunk.Content)
            .IsRequired();

        builder.Property(chunk => chunk.TokenCount)
            .IsRequired();

        builder.Property(chunk => chunk.SectionPath)
            .HasMaxLength(1024);

        builder.Property(chunk => chunk.EmbeddingModel)
            .HasMaxLength(256);

        builder.Property(chunk => chunk.CreatedBy).HasMaxLength(256);
        builder.Property(chunk => chunk.ModifiedBy).HasMaxLength(256);

        // Optional owned type: PageRange is null for formats without pages, so both
        // columns are nullable and EF uses them to decide whether the value object exists.
        builder.OwnsOne(chunk => chunk.PageRange, page =>
        {
            page.Property(range => range.Start).HasColumnName("PageStart");
            page.Property(range => range.End).HasColumnName("PageEnd");
        });

        // ══ THE RELOCATED INVARIANT ═══════════════════════════════════════════════════
        // Step 2's Document.AddChunks enforces that indexes are unique and contiguous, but
        // Step 8 writes chunks in bulk for large documents and bypasses that method for
        // performance. An invariant that can be bypassed is not an invariant, so it moves
        // here — to a constraint the database will not let anything violate, including a
        // retried job racing the original.
        //
        // Recorded in docs/PROGRESS.md under "Invariants enforced outside the aggregate".
        builder.HasIndex(chunk => new { chunk.DocumentId, chunk.Index })
            .IsUnique()
            .HasDatabaseName("UX_DocumentChunks_DocumentId_ChunkIndex");
    }
}
