using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EKA.IntegrationTests.Persistence;

/// <summary>
/// Step 3's exit criterion: the migration applies to a <b>fresh</b> database.
/// </summary>
/// <remarks>
/// Applying cleanly to the developer's existing database proves nothing — that database
/// accumulated its schema one migration at a time and may carry state no new environment
/// will have. The only meaningful test creates an empty database and migrates it from
/// nothing, which is what a new environment and CI actually do.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class SchemaMigrationTests
{
    private readonly SqlServerFixture _fixture;

    public SchemaMigrationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task The_migration_applies_to_a_completely_empty_database()
    {
        var databaseName = $"eka_clean_{Guid.NewGuid():N}";
        var connectionString = await _fixture.CreateEmptyDatabaseAsync(databaseName);

        await using var context = _fixture.CreateContext(connectionStringOverride: connectionString);

        await context.Database.MigrateAsync();

        (await context.Database.GetAppliedMigrationsAsync()).ShouldNotBeEmpty();
        (await context.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Documents")]
    [InlineData("DocumentChunks")]
    [InlineData("Conversations")]
    [InlineData("Messages")]
    [InlineData("MessageCitations")]
    [InlineData("PromptTemplates")]
    [InlineData("AIProviders")]
    [InlineData("Users")]
    public async Task Every_expected_table_exists(string tableName)
    {
        await using var context = _fixture.CreateContext();

        var exists = await TableExistsAsync(context, tableName);

        exists.ShouldBeTrue($"table {tableName} should have been created by the migration");
    }

    [Theory]
    // The two invariants Step 2 relocated out of the aggregates. If either of these
    // indexes is missing, the rule it carries is simply not enforced anywhere.
    [InlineData("UX_DocumentChunks_DocumentId_ChunkIndex", "DocumentChunks", true)]
    [InlineData("UX_PromptTemplates_Name_Active", "PromptTemplates", true)]
    [InlineData("UX_PromptTemplates_Name_Version", "PromptTemplates", true)]
    [InlineData("UX_Users_ExternalId", "Users", true)]
    [InlineData("UX_AIProviders_Name", "AIProviders", true)]
    [InlineData("IX_Documents_ContentHash", "Documents", false)]
    [InlineData("IX_Documents_TenantId_Status", "Documents", false)]
    [InlineData("IX_Conversations_TenantId_UserId_ArchivedAt", "Conversations", false)]
    [InlineData("IX_MessageCitations_DocumentId", "MessageCitations", false)]
    [InlineData("IX_Users_TenantId_Email", "Users", false)]
    public async Task Every_expected_index_exists_with_the_right_uniqueness(
        string indexName,
        string tableName,
        bool shouldBeUnique)
    {
        await using var context = _fixture.CreateContext();

        var isUnique = await ScalarAsync<bool?>(
            context,
            """
            SELECT CAST(i.is_unique AS bit)
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            WHERE i.name = @indexName AND t.name = @tableName
            """,
            ("@indexName", indexName),
            ("@tableName", tableName));

        isUnique.ShouldNotBeNull($"index {indexName} should exist on {tableName}");
        isUnique.Value.ShouldBe(shouldBeUnique);
    }

    [Fact]
    public async Task The_active_prompt_template_index_is_filtered()
    {
        // A plain unique index on Name would permit only ONE version per template, which
        // would break versioning entirely. The filter is what makes "many versions, at
        // most one active" expressible.
        await using var context = _fixture.CreateContext();

        var filter = await ScalarAsync<string?>(
            context,
            """
            SELECT i.filter_definition
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            WHERE i.name = 'UX_PromptTemplates_Name_Active' AND t.name = 'PromptTemplates'
            """);

        filter.ShouldNotBeNull();
        filter.ShouldContain("IsActive");
    }

    [Fact]
    public async Task The_content_hash_column_is_narrow_fixed_width_and_non_unicode()
    {
        // A SHA-256 hex digest is always 64 ASCII characters. nvarchar(max) would double
        // the storage and make the idempotency index impossible.
        await using var context = _fixture.CreateContext();

        var dataType = await ScalarAsync<string?>(
            context,
            """
            SELECT DATA_TYPE + '(' + CAST(CHARACTER_MAXIMUM_LENGTH AS varchar(10)) + ')'
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = 'Documents' AND COLUMN_NAME = 'ContentHash'
            """);

        dataType.ShouldBe("char(64)");
    }

    private static async Task<bool> TableExistsAsync(DbContext context, string tableName)
    {
        var result = await ScalarAsync<int?>(
            context,
            """
            SELECT 1 FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_NAME = @tableName AND TABLE_TYPE = 'BASE TABLE'
            """,
            ("@tableName", tableName));

        return result is 1;
    }

    private static async Task<T?> ScalarAsync<T>(
        DbContext context,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var connection = context.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync();

        return result is null or DBNull ? default : (T)result;
    }
}
