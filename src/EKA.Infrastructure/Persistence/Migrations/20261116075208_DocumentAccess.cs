using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EKA.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited. EF generated `nullable: false, defaultValue: "[]"`, which would have
            // given every existing document an EMPTY access list: readable by nobody, and
            // unreadable by the domain too (DocumentAccess refuses an empty list). Existing
            // documents were tenant-wide in practice, so that is what they become.
            //
            // Nullable, backfill, then NOT NULL: no default constraint is left behind. A
            // default of '["tenant"]' would quietly make any row inserted outside EF readable by
            // the whole tenant; with none, such an insert fails, which is the safe direction.
            migrationBuilder.AddColumn<string>(
                name: "Access",
                table: "Documents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.Sql("""UPDATE [Documents] SET [Access] = N'["tenant"]' WHERE [Access] IS NULL;""");

            migrationBuilder.AlterColumn<string>(
                name: "Access",
                table: "Documents",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Access",
                table: "Documents");
        }
    }
}
