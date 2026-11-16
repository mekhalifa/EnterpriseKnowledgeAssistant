using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EKA.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantTokenUsage",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Period = table.Column<string>(type: "char(7)", unicode: false, fixedLength: true, maxLength: 7, nullable: false),
                    ChatInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ChatOutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    EmbeddingTokens = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantTokenUsage", x => new { x.TenantId, x.Period });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantTokenUsage");
        }
    }
}
