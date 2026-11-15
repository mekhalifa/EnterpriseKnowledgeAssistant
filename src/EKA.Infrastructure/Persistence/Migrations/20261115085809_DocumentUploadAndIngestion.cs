using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EKA.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Step 7: the same bytes may be registered once per tenant, enforced by the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The unique index is written by hand,</b> and is absent from the model snapshot. EF
    /// Core cannot declare an index spanning an owner's property (<c>TenantId</c> on
    /// Document) and an owned type's property (<c>ContentHash</c> on FileMetadata), although
    /// both are columns of this one table. Because the model never knew about the index, a
    /// future migration will never try to drop it. <c>SchemaMigrationTests</c> pins its
    /// existence and uniqueness, which is what makes the hand-written index safe to depend on.
    /// </para>
    /// <para>
    /// TenantId leads, so the index also serves the handler's tenant-scoped lookup
    /// (<c>FindByContentHashAsync</c>), which makes the old single-column index redundant.
    /// </para>
    /// <para>
    /// <b>On an existing database with duplicates</b> (possible, since Step 4 only checked in
    /// the handler) the CREATE fails. That failure is correct, because those rows are exactly
    /// the race this index exists to stop. Resolve them by hand before applying it.
    /// </para>
    /// </remarks>
    public partial class DocumentUploadAndIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documents_ContentHash",
                table: "Documents");

            migrationBuilder.CreateIndex(
                name: "UX_Documents_TenantId_ContentHash",
                table: "Documents",
                columns: new[] { "TenantId", "ContentHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Documents_TenantId_ContentHash",
                table: "Documents");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_ContentHash",
                table: "Documents",
                column: "ContentHash");
        }
    }
}
