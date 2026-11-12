using EKA.Domain.Conversations;
using EKA.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EKA.Infrastructure.Persistence.Configurations;

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages");

        builder.HasKey(message => message.Id);

        builder.Property(message => message.ConversationId).IsRequired();

        builder.Property(message => message.Role)
            .HasConversion<int>()
            .IsRequired();

        // nvarchar(max): questions and answers have no sensible ceiling.
        builder.Property(message => message.Content)
            .IsRequired();

        builder.Property(message => message.ProviderName).HasMaxLength(128);
        builder.Property(message => message.ModelName).HasMaxLength(256);

        builder.Property(message => message.CreatedBy).HasMaxLength(256);
        builder.Property(message => message.ModifiedBy).HasMaxLength(256);

        // ── Citations: an owned COLLECTION, so a separate table rather than JSON.
        //    JSON would be simpler, but Step 11 needs to query citations to measure
        //    citation correctness ("which documents does the assistant actually cite?"),
        //    and a relational table answers that with an ordinary join instead of
        //    OPENJSON gymnastics.
        builder.OwnsMany(message => message.Citations, citation =>
        {
            citation.ToTable("MessageCitations");

            citation.WithOwner().HasForeignKey("MessageId");

            // Citations are value objects with no natural identity, so EF needs a shadow
            // key. Composite with MessageId keeps rows clustered with their message.
            citation.Property<int>("Id").ValueGeneratedOnAdd();
            citation.HasKey("MessageId", "Id");

            citation.Property(reference => reference.DocumentId).IsRequired();

            citation.Property(reference => reference.DocumentTitle)
                .HasMaxLength(DocumentTitle.MaxLength)
                .IsRequired();

            citation.Property(reference => reference.ChunkIndex)
                .HasColumnName("ChunkIndex")
                .IsRequired();

            citation.Property(reference => reference.Score)
                .HasColumnName("SimilarityScore")
                .IsRequired();

            citation.Property(reference => reference.SectionPath).HasMaxLength(1024);

            citation.OwnsOne(reference => reference.PageRange, page =>
            {
                page.Property(range => range.Start).HasColumnName("PageStart");
                page.Property(range => range.End).HasColumnName("PageEnd");
            });

            // Answers "which documents get cited most" without scanning every message.
            citation.HasIndex(reference => reference.DocumentId)
                .HasDatabaseName("IX_MessageCitations_DocumentId");
        });

        builder.Navigation(message => message.Citations)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
