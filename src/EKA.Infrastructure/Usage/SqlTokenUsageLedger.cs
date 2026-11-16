using EKA.Application.Abstractions.Usage;
using EKA.Domain.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EKA.Infrastructure.Usage;

/// <summary>One tenant-month of usage, as stored. Infrastructure-only: the domain never sees it.</summary>
internal sealed class TenantTokenUsageRow
{
    public Guid TenantId { get; set; }

    /// <summary><c>yyyy-MM</c>, UTC.</summary>
    public string Period { get; set; } = string.Empty;

    public long ChatInputTokens { get; set; }

    public long ChatOutputTokens { get; set; }

    public long EmbeddingTokens { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class TenantTokenUsageConfiguration : IEntityTypeConfiguration<TenantTokenUsageRow>
{
    public void Configure(EntityTypeBuilder<TenantTokenUsageRow> builder)
    {
        builder.ToTable("TenantTokenUsage");
        builder.HasKey(row => new { row.TenantId, row.Period });
        builder.Property(row => row.Period).HasMaxLength(7).IsFixedLength().IsUnicode(false);
    }
}

/// <summary>
/// <see cref="ITokenUsageLedger"/> over SQL Server: one atomic upsert per recorded call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own connection, not the request's <c>DbContext</c>.</b> Indexing embeds batches
/// concurrently, and a <c>DbContext</c> used from two threads at once throws. Usage is also
/// meant to be written outside the request's unit of work (see the port). A short-lived pooled
/// connection per call gives both.
/// </para>
/// <para>
/// <b><c>MERGE … WITH (HOLDLOCK)</c></b> makes "insert the month's row, or add to it" one atomic
/// statement. Without HOLDLOCK, two first-of-the-month requests can both see no row and both
/// insert, and one fails on the primary key. Without MERGE, a read-then-update loses
/// increments under concurrency. A test fires concurrent increments and checks the exact sum.
/// </para>
/// </remarks>
internal sealed class SqlTokenUsageLedger(string connectionString) : ITokenUsageLedger
{
    public async Task AddAsync(TenantId tenantId, UsagePeriod period, TokenUsage usage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usage);

        const string Sql = """
            MERGE [TenantTokenUsage] WITH (HOLDLOCK) AS target
            USING (SELECT @tenant AS TenantId, @period AS Period) AS source
               ON target.[TenantId] = source.TenantId AND target.[Period] = source.Period
            WHEN MATCHED THEN UPDATE SET
                [ChatInputTokens] = target.[ChatInputTokens] + @chatIn,
                [ChatOutputTokens] = target.[ChatOutputTokens] + @chatOut,
                [EmbeddingTokens] = target.[EmbeddingTokens] + @embedding,
                [UpdatedAt] = SYSDATETIMEOFFSET()
            WHEN NOT MATCHED THEN
                INSERT ([TenantId], [Period], [ChatInputTokens], [ChatOutputTokens], [EmbeddingTokens], [UpdatedAt])
                VALUES (@tenant, @period, @chatIn, @chatOut, @embedding, SYSDATETIMEOFFSET());
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.Value);
        command.Parameters.Add(new SqlParameter("@period", System.Data.SqlDbType.Char, 7) { Value = period.ToString() });
        command.Parameters.AddWithValue("@chatIn", usage.ChatInputTokens);
        command.Parameters.AddWithValue("@chatOut", usage.ChatOutputTokens);
        command.Parameters.AddWithValue("@embedding", usage.EmbeddingTokens);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TokenUsage> GetAsync(TenantId tenantId, UsagePeriod period, CancellationToken cancellationToken = default)
    {
        const string Sql = """
            SELECT [ChatInputTokens], [ChatOutputTokens], [EmbeddingTokens]
            FROM [TenantTokenUsage]
            WHERE [TenantId] = @tenant AND [Period] = @period;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.Value);
        command.Parameters.Add(new SqlParameter("@period", System.Data.SqlDbType.Char, 7) { Value = period.ToString() });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new TokenUsage(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2))
            : TokenUsage.None;
    }
}
