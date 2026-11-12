using EKA.Domain.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EKA.Infrastructure.Persistence.Configurations;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("Conversations");

        builder.HasKey(conversation => conversation.Id);

        builder.Property(conversation => conversation.TenantId).IsRequired();
        builder.Property(conversation => conversation.UserId).IsRequired();

        builder.Property(conversation => conversation.Title)
            .HasMaxLength(Conversation.MaxTitleLength);

        builder.Property(conversation => conversation.CreatedBy).HasMaxLength(256);
        builder.Property(conversation => conversation.ModifiedBy).HasMaxLength(256);

        builder.HasMany(conversation => conversation.Messages)
            .WithOne()
            .HasForeignKey(message => message.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(conversation => conversation.Messages)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // Covers the sidebar query: this tenant, this user, not archived, most recently
        // touched first. Both TenantId and UserId are in the index because conversations
        // are private to one person — scoping on tenant alone would let one colleague read
        // another's history.
        builder.HasIndex(conversation => new
        {
            conversation.TenantId,
            conversation.UserId,
            conversation.ArchivedAt,
        })
            .HasDatabaseName("IX_Conversations_TenantId_UserId_ArchivedAt");
    }
}
