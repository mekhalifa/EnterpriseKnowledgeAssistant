using EKA.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EKA.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(user => user.Id);

        builder.Property(user => user.TenantId).IsRequired();

        builder.Property(user => user.ExternalId)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(user => user.Email)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(user => user.DisplayName)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(user => user.IsActive).IsRequired();

        builder.Property(user => user.CreatedBy).HasMaxLength(256);
        builder.Property(user => user.ModifiedBy).HasMaxLength(256);

        // ── Roles: a primitive collection, stored as a JSON array in one column.
        //
        //    Why JSON is adequate here, given roles feed the Step 9 ACL filter: that
        //    filter runs against Qdrant, not SQL. The flow is "load the user once, pass
        //    their role list as a parameter into the vector-store filter" — so roles never
        //    need to be joined or indexed relationally. A normalized UserRoles table would
        //    add a table and a join for a query nobody makes.
        //
        //    If role-based filtering ever moves into SQL, this becomes a real table and
        //    the change deserves an ADR.
        builder.PrimitiveCollection(user => user.Roles)
            .HasColumnName("Roles")
            .HasMaxLength(4000)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // The sign-in path: a token arrives carrying a subject claim, and the local
        // projection is found by it. Unique because two local users for one identity would
        // split a person's history in half.
        builder.HasIndex(user => user.ExternalId)
            .IsUnique()
            .HasDatabaseName("UX_Users_ExternalId");

        // Not unique: the same address can legitimately exist in two tenants.
        builder.HasIndex(user => new { user.TenantId, user.Email })
            .HasDatabaseName("IX_Users_TenantId_Email");
    }
}
