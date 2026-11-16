using EKA.Application.Abstractions.Usage;
using EKA.Application.Common;
using EKA.Contracts.Usage;
using EKA.Domain.Access;
using Microsoft.Extensions.Options;

namespace EKA.Application.Usage;

/// <summary>The caller's tenant's usage this month. Administrators only.</summary>
public sealed record GetUsageQuery(AccessScope Caller);

/// <summary>
/// Reports usage against budget for the caller's own tenant (Step 12).
/// </summary>
/// <remarks>
/// Administrators only. Usage reveals how much the organization uses the assistant, which is
/// business information, not something every employee needs. 403 for others, because the
/// endpoint's existence is no secret.
/// </remarks>
public sealed class GetUsageHandler(ITokenUsageLedger ledger, IOptions<TokenBudgetOptions> options, TimeProvider timeProvider)
{
    public async Task<Result<UsageResponse>> HandleAsync(GetUsageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!query.Caller.IsAdministrator)
        {
            return Error.Forbidden("usage.administrators_only", "Only an administrator can view the tenant's usage.");
        }

        var period = UsagePeriod.Of(timeProvider.GetUtcNow());
        var used = await ledger.GetAsync(query.Caller.TenantId, period, cancellationToken).ConfigureAwait(false);
        var budget = options.Value.For(query.Caller.TenantId);

        return new UsageResponse(
            period.ToString(),
            used.ChatInputTokens,
            used.ChatOutputTokens,
            used.EmbeddingTokens,
            used.Total,
            budget,
            Math.Max(0, budget - used.Total));
    }
}
