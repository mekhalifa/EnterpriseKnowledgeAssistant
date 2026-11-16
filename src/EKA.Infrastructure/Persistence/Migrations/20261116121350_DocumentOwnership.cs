using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EKA.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Step 12: documents record their owner, who (with tenant administrators) may manage them.
    /// </summary>
    /// <remarks>
    /// Hand-edited to backfill. <c>CreatedBy</c> has held the uploader's subject since Step 4
    /// (the audit interceptor writes it from the authenticated caller), so it is the owner of every
    /// existing document. Rows written by a non-person ("system", "migration") stay unowned, and
    /// only an administrator can manage those. That is the safe direction: no one gains
    /// management rights by accident.
    /// </remarks>
    public partial class DocumentOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerSubject",
                table: "Documents",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE [Documents]
                SET [OwnerSubject] = [CreatedBy]
                WHERE [CreatedBy] IS NOT NULL AND [CreatedBy] NOT IN (N'system', N'migration');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerSubject",
                table: "Documents");
        }
    }
}
