using System.Globalization;
using EKA.Domain.Common;

namespace EKA.Application.Abstractions.Usage;

/// <summary>
/// Running totals of provider tokens per tenant per calendar month (Step 12): what the tenant
/// has cost, and what its budget is checked against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Increments must be atomic.</b> Two answers finishing at once both add to the same row.
/// A read-modify-write through the unit of work would lose one of them, and an
/// under-counting ledger is a budget that does not hold. The adapter does a single atomic
/// upsert per call.
/// </para>
/// <para>
/// <b>Written as soon as the cost is incurred,</b> not with the rest of the request's changes.
/// If saving the answer fails afterwards, the tokens were still paid for, so they still count.
/// </para>
/// </remarks>
public interface ITokenUsageLedger
{
    Task AddAsync(TenantId tenantId, UsagePeriod period, TokenUsage usage, CancellationToken cancellationToken = default);

    /// <returns>The month's totals; zero when nothing was recorded.</returns>
    Task<TokenUsage> GetAsync(TenantId tenantId, UsagePeriod period, CancellationToken cancellationToken = default);
}

/// <summary>Tokens by kind. Chat input and output are priced differently, so they are kept apart.</summary>
public sealed record TokenUsage(long ChatInputTokens, long ChatOutputTokens, long EmbeddingTokens)
{
    public static TokenUsage None { get; } = new(0, 0, 0);

    public long Total => ChatInputTokens + ChatOutputTokens + EmbeddingTokens;
}

/// <summary>A calendar month in UTC, written <c>yyyy-MM</c>. The unit a budget is set for.</summary>
public readonly record struct UsagePeriod(int Year, int Month)
{
    public static UsagePeriod Of(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new UsagePeriod(utc.Year, utc.Month);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month:D2}");
}
