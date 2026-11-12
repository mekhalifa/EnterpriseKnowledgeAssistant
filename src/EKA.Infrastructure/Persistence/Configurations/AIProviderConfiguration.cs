using EKA.Domain.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EKA.Infrastructure.Persistence.Configurations;

internal sealed class AIProviderConfiguration : IEntityTypeConfiguration<AIProvider>
{
    public void Configure(EntityTypeBuilder<AIProvider> builder)
    {
        builder.ToTable("AIProviders");

        builder.HasKey(provider => provider.Id);

        builder.Property(provider => provider.Name)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(provider => provider.Kind)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(provider => provider.ChatModel)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(provider => provider.EmbeddingModel)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(provider => provider.EmbeddingDimensions)
            .IsRequired();

        // The NAME of a secret, never a secret. The entity has no property that could
        // hold a credential, and a reflection test in EKA.Domain.Tests fails the build if
        // one is ever added — so there is nothing here for this column to leak.
        builder.Property(provider => provider.SecretReference)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(provider => provider.Endpoint)
            .HasMaxLength(512);

        builder.Property(provider => provider.CreatedBy).HasMaxLength(256);
        builder.Property(provider => provider.ModifiedBy).HasMaxLength(256);

        builder.HasIndex(provider => provider.Name)
            .IsUnique()
            .HasDatabaseName("UX_AIProviders_Name");
    }
}
