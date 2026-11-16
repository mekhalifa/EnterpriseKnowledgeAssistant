using System.Collections.Concurrent;
using EKA.Application.Abstractions.AI;
using EKA.Application.Abstractions.Usage;
using EKA.Application.Usage;
using EKA.Domain.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EKA.Application.Tests.Fakes;

/// <summary>In-memory ledger, keyed like the SQL one: tenant and month. Thread-safe, because
/// indexing records batches concurrently.</summary>
internal sealed class FakeTokenUsageLedger : ITokenUsageLedger
{
    private readonly ConcurrentDictionary<(TenantId, UsagePeriod), TokenUsage> _usage = new();

    /// <summary>When set, recording throws, as an unreachable database would.</summary>
    public bool FailWrites { get; set; }

    public Task AddAsync(TenantId tenantId, UsagePeriod period, TokenUsage usage, CancellationToken cancellationToken = default)
    {
        if (FailWrites)
        {
            throw new IOException("ledger unreachable");
        }

        _usage.AddOrUpdate(
            (tenantId, period),
            usage,
            (_, existing) => new TokenUsage(
                existing.ChatInputTokens + usage.ChatInputTokens,
                existing.ChatOutputTokens + usage.ChatOutputTokens,
                existing.EmbeddingTokens + usage.EmbeddingTokens));

        return Task.CompletedTask;
    }

    public Task<TokenUsage> GetAsync(TenantId tenantId, UsagePeriod period, CancellationToken cancellationToken = default) =>
        Task.FromResult(_usage.TryGetValue((tenantId, period), out var usage) ? usage : TokenUsage.None);

    /// <summary>This month's usage for a tenant, by the fixed test clock.</summary>
    public TokenUsage For(TenantId tenantId) =>
        _usage.TryGetValue((tenantId, UsagePeriod.Of(FixedTimeProvider.DefaultNow)), out var usage) ? usage : TokenUsage.None;
}

internal static class Budgets
{
    public static TokenBudget For(ITokenUsageLedger ledger, long monthlyTokens = 1_000_000) =>
        new(
            ledger,
            new EstimatingTokenCounter(),
            Options.Create(new TokenBudgetOptions { MonthlyTokens = monthlyTokens }),
            FixedTimeProvider.Default,
            NullLogger<TokenBudget>.Instance);

    /// <summary>A budget that is never exhausted and whose records nobody reads.</summary>
    public static TokenBudget Unlimited() => For(new FakeTokenUsageLedger(), long.MaxValue);
}
