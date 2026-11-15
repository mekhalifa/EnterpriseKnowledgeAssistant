using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EKA.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Step 6: user uniqueness becomes per tenant, and the first prompt template is seeded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the seed is InsertData here and not HasData in the model.</b> HasData makes the
    /// row part of the model: every later migration diffs it, and an operator who deactivates
    /// v1 in favour of v2 is then fighting the snapshot. A one-off insert records the fact
    /// that it happened, and is otherwise data like any other. A new prompt version is a new
    /// migration that inserts v2 and flips <c>IsActive</c>.
    /// </para>
    /// <para>
    /// <b>The provider row exists because the template's foreign key requires one.</b> Since
    /// Step 5 the live provider comes from the <c>Ai</c> configuration section, not from this
    /// table (ADR-0015). The row records what the prompt was written for. It does not choose
    /// what answers. Reconciling the two is an open decision in PROGRESS.
    /// </para>
    /// </remarks>
    public partial class ConversationsAndDefaultPrompt : Migration
    {
        private static readonly Guid ProviderId = new("0e5a0001-0000-4000-8000-000000000001");
        private static readonly Guid DefaultTemplateId = new("0e5a0002-0000-4000-8000-000000000001");
        private static readonly DateTimeOffset SeededAt = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// The v1 system instruction. Retrieval does not exist until Step 9, so the honest
        /// instruction is to say so. A general-purpose assistant that answers policy
        /// questions from its training data is the failure this product exists to prevent.
        /// </summary>
        private const string SystemPrompt =
            "You are the Enterprise Knowledge Assistant, answering questions for employees of one organization.\n"
            + "\n"
            + "Document search is not connected yet: you cannot see any of the organization's documents. "
            + "When a question depends on the organization's own policies, figures, people or systems, say plainly "
            + "that you cannot see its documents yet, instead of guessing.\n"
            + "\n"
            + "Never invent policies, numbers, names, dates or citations. If you do not know, say you do not know.\n"
            + "Answer concisely, in the language of the question.";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Users_ExternalId",
                table: "Users");

            migrationBuilder.CreateIndex(
                name: "UX_Users_TenantId_ExternalId",
                table: "Users",
                columns: new[] { "TenantId", "ExternalId" },
                unique: true);

            migrationBuilder.InsertData(
                table: "AIProviders",
                columns: new[]
                {
                    "Id", "Name", "Kind", "ChatModel", "EmbeddingModel", "EmbeddingDimensions",
                    "SecretReference", "Endpoint", "IsEnabled", "CreatedAt", "CreatedBy",
                },
                values: new object[]
                {
                    ProviderId, "openrouter", 30 /* AIProviderKind.OpenAICompatible */,
                    "openai/gpt-4o-mini", "openai/text-embedding-3-small", 1536,
                    "Ai:ApiKey", "https://openrouter.ai/api/v1", true, SeededAt, "migration",
                });

            // {{question}} only, no {{context}}: v1 is ungrounded. Step 10 inserts a v2 with
            // {{context}} and activates it, and that switch is the point of versioning.
            migrationBuilder.InsertData(
                table: "PromptTemplates",
                columns: new[]
                {
                    "Id", "Name", "Version", "SystemPrompt", "UserPromptTemplate", "AIProviderId",
                    "IsActive", "CreatedAt", "CreatedBy",
                },
                values: new object[]
                {
                    DefaultTemplateId, "default", 1, SystemPrompt, "{{question}}", ProviderId,
                    true, SeededAt, "migration",
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(table: "PromptTemplates", keyColumn: "Id", keyValue: DefaultTemplateId);
            migrationBuilder.DeleteData(table: "AIProviders", keyColumn: "Id", keyValue: ProviderId);

            migrationBuilder.DropIndex(
                name: "UX_Users_TenantId_ExternalId",
                table: "Users");

            // Fails if two tenants already share a subject id. That failure is correct: it is
            // precisely the data the global index cannot represent.
            migrationBuilder.CreateIndex(
                name: "UX_Users_ExternalId",
                table: "Users",
                column: "ExternalId",
                unique: true);
        }
    }
}
