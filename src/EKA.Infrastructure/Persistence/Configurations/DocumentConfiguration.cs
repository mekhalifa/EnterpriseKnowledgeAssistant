using EKA.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EKA.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Document"/>.
/// </summary>
/// <remarks>
/// All mapping lives here, never as attributes on the entity. <c>[Table]</c>, <c>[Key]</c>,
/// or <c>[MaxLength]</c> on <see cref="Document"/> would make <c>EKA.Domain</c> depend on
/// EF Core, and the architecture tests would fail the build — correctly.
/// </remarks>
internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("Documents");

        builder.HasKey(document => document.Id);

        builder.Property(document => document.TenantId)
            .IsRequired();

        // Conversion and max length come from EkaDbContext.ConfigureConventions.
        builder.Property(document => document.Title)
            .IsRequired();

        builder.Property(document => document.StorageKey)
            .HasMaxLength(1024)
            .IsRequired();

        // Stored as int. IngestionStatus declares explicit numeric values precisely so
        // that inserting a pipeline stage later does not renumber existing rows.
        builder.Property(document => document.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(document => document.Authority)
            .IsRequired();

        builder.Property(document => document.FailureReason)
            .HasMaxLength(2000);

        builder.Property(document => document.CreatedBy).HasMaxLength(256);
        builder.Property(document => document.ModifiedBy).HasMaxLength(256);

        // ── FileMetadata: a composite value object, so an owned type rather than a
        //    converter. Owned-by-default means these become columns on Documents, not a
        //    separate table — four facts about one file belong on the file's row.
        builder.OwnsOne(document => document.File, file =>
        {
            file.Property(metadata => metadata.FileName)
                .HasColumnName("FileName")
                .HasMaxLength(512)
                .IsRequired();

            file.Property(metadata => metadata.ContentType)
                .HasColumnName("ContentType")
                .HasMaxLength(255)
                .IsRequired();

            file.Property(metadata => metadata.SizeBytes)
                .HasColumnName("FileSizeBytes")
                .IsRequired();

            // Fixed length because a SHA-256 hex digest is always exactly 64 characters.
            // char(64) is narrower and compares faster than nvarchar for an index this is
            // probed on during every upload.
            file.Property(metadata => metadata.ContentHash)
                .HasColumnName("ContentHash")
                .HasMaxLength(64)
                .IsFixedLength()
                .IsUnicode(false)
                .IsRequired();

            // Supports the idempotency check: the same bytes uploaded twice must not
            // become two indexed documents. Not unique — a tenant legitimately re-uploads
            // after a Reset, and two tenants may hold the same file.
            file.HasIndex(metadata => metadata.ContentHash)
                .HasDatabaseName("IX_Documents_ContentHash");
        });

        builder.Navigation(document => document.File).IsRequired();

        // ── Chunks: the aggregate's children. Cascade delete because a chunk has no
        //    meaning without its document.
        builder.HasMany(document => document.Chunks)
            .WithOne()
            .HasForeignKey(chunk => chunk.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // The getter returns `_chunks.AsReadOnly()`, which EF cannot add to. Telling EF to
        // use the backing field means it writes `_chunks` directly and never calls the
        // getter — which is what lets the domain keep its encapsulation without a
        // persistence-shaped compromise.
        builder.Navigation(document => document.Chunks)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // The ingestion worker polls for pending work; tenant plus status covers both that
        // query and the per-tenant document list.
        builder.HasIndex(document => new { document.TenantId, document.Status })
            .HasDatabaseName("IX_Documents_TenantId_Status");
    }
}
