using EKA.Domain.Prompting;
using EKA.Domain.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EKA.Infrastructure.Persistence.Configurations;

internal sealed class PromptTemplateConfiguration : IEntityTypeConfiguration<PromptTemplate>
{
    public void Configure(EntityTypeBuilder<PromptTemplate> builder)
    {
        builder.ToTable("PromptTemplates");

        builder.HasKey(template => template.Id);

        builder.Property(template => template.Name)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(template => template.Version)
            .IsRequired();

        // nvarchar(max): prompts grow, and a truncated system instruction would silently
        // drop the refusal policy — which is the part keeping answers honest.
        builder.Property(template => template.SystemPrompt).IsRequired();
        builder.Property(template => template.UserPromptTemplate).IsRequired();

        builder.Property(template => template.AIProviderId).IsRequired();

        builder.Property(template => template.CreatedBy).HasMaxLength(256);
        builder.Property(template => template.ModifiedBy).HasMaxLength(256);

        // A template is written for a specific provider (README P1). Restrict rather than
        // cascade: deleting a provider that templates still reference should fail loudly,
        // not silently delete the prompt engineering work.
        builder.HasOne<AIProvider>()
            .WithMany()
            .HasForeignKey(template => template.AIProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        // One row per name+version. Creating version 2 twice is a bug, not a variant.
        builder.HasIndex(template => new { template.Name, template.Version })
            .IsUnique()
            .HasDatabaseName("UX_PromptTemplates_Name_Version");

        // ══ THE SECOND RELOCATED INVARIANT ════════════════════════════════════════════
        // "At most one ACTIVE version per name" spans multiple PromptTemplate instances,
        // so no single aggregate can see enough to enforce it — Activate() can only know
        // about itself. A FILTERED unique index enforces it in the one place that sees all
        // rows: unique on Name, but only among rows where IsActive = 1, so any number of
        // inactive versions may coexist.
        //
        // This is also why the repository exposes GetActiveAsync returning a single
        // template rather than a list: the database guarantees there is at most one.
        //
        // Recorded in docs/PROGRESS.md under "Invariants enforced outside the aggregate".
        builder.HasIndex(template => template.Name)
            .IsUnique()
            .HasFilter("[IsActive] = 1")
            .HasDatabaseName("UX_PromptTemplates_Name_Active");
    }
}
