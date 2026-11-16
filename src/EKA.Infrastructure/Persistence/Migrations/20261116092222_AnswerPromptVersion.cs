using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EKA.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AnswerPromptVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PromptTemplateVersion",
                table: "Messages",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PromptTemplateVersion",
                table: "Messages");
        }
    }
}
