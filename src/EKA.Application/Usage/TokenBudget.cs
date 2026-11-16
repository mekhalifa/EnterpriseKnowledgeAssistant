using EKA.Application.Abstractions.AI;
using EKA.Application.Abstractions.Usage;
using EKA.Application.Common;
using EKA.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EKA.Application.Usage;

/// <summary>The <c>Budgets</c> section: monthly token budgets per tenant (Step 12, ADR-0032).</summary>
public sealed class TokenBudgetOptions
{
    public const string SectionName = "Budgets";

    /// <summary>
    /// Tokens a tenant may spend per calendar month (chat input, chat output and embeddings
    /// together), unless <see cref="Tenants"/> says otherwise. Two million is about 1.5 USD a
    /// month of gpt-4o-mini plus small embeddings: generous for a team, and a hard stop for a
    /// runaway script.
    /// </summary>
    public long MonthlyTokens { get; set; } = 2_000_000;

    /// <summary>Per-tenant overrides, keyed by tenant id. A value of 0 suspends the tenant's AI use.</summary>
    /// <remarks>
    /// <b>String keys, on purpose.</b> The configuration binder silently drops entries of a
    /// <c>Dictionary&lt;Guid, long&gt;</c>: an operator's "suspend this tenant" would bind to
    /// nothing and the tenant would keep spending (found by step-12's budget test). Keys are
    /// validated as GUIDs at startup instead, so a typo fails loudly.
    /// </remarks>
    public Dictionary<string, long> Tenants { get; set; } = [];

    public long For(TenantId tenantId) =>
        Tenants.FirstOrDefault(pair => Guid.TryParse(pair.Key, out var id) && id == tenantId.Value) is { Key: not null } match
            ? match.Value
            : MonthlyTokens;
}

public sealed class TokenBudgetOptionsValidator : IValidateOptions<TokenBudgetOptions>
{
    public ValidateOptionsResult Validate(string? name, TokenBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.MonthlyTokens < 0 || options.Tenants.Values.Any(budget => budget < 0))
        {
            failures.Add($"{TokenBudgetOptions.SectionName}: budgets cannot be negative.");
        }

        failures.AddRange(options.Tenants.Keys
            .Where(key => !Guid.TryParse(key, out _))
            .Select(key => $"{TokenBudgetOptions.SectionName}:Tenants:{key} is not a tenant id (a GUID). The override would apply to nobody."));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Checks a tenant's monthly budget before provider calls, and records what each call cost
/// (Step 12).
/// </summary>
/// <remarks>
/// <para>
/// <b>A soft limit, knowingly.</b> The check reads the total, and the spend is recorded after the
/// call. Concurrent requests that all pass the check can overshoot the budget by what they cost
/// together: a few thousand tokens. A hard limit would need a reservation before every call and
/// a release after, which is a lot of machinery for a few cents of accuracy.
/// </para>
/// <para>
/// <b>Provider counts first, estimates second.</b> Providers usually report tokens. When they do
/// not, the conservative estimate (ADR-0018) is recorded instead of zero: an under-counting
/// ledger is a budget that leaks.
/// </para>
/// <para>
/// <b>Recording never fails the user's request.</b> The answer was already produced and paid
/// for. A ledger outage is logged as an error for an operator, and the request completes. The
/// budget is then under-counted by that call, which is the lesser harm.
/// </para>
/// </remarks>
public sealed partial class TokenBudget(
    ITokenUsageLedger ledger,
    ITokenCounter tokens,
    IOptions<TokenBudgetOptions> options,
    TimeProvider timeProvider,
    ILogger<TokenBudget> logger)
{
    /// <returns>Success when the tenant has budget left this month; otherwise
    /// <c>usage.budget_exhausted</c> (429).</returns>
    public async Task<Result> EnsureAvailableAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        var budget = options.Value.For(tenantId);
        var used = await ledger.GetAsync(tenantId, UsagePeriod.Of(timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);

        return used.Total < budget
            ? Result.Success()
            : LimitErrors.Exceeded(
                "usage.budget_exhausted",
                "Your organization's monthly AI usage budget has been reached. Ask an administrator to raise it, or try again next month.");
    }

    public Task RecordChatAsync(TenantId tenantId, ChatCompletion completion, int estimatedInputTokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);

        return RecordAsync(
            tenantId,
            new TokenUsage(
                completion.InputTokens?.Value ?? estimatedInputTokens,
                completion.OutputTokens?.Value ?? tokens.Count(completion.Content).Value,
                0),
            cancellationToken);
    }

    public Task RecordEmbeddingAsync(TenantId tenantId, EmbeddingBatch batch, IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(inputs);

        var spent = batch.InputTokens?.Value ?? inputs.Sum(input => (long)tokens.Count(input).Value);
        return RecordAsync(tenantId, new TokenUsage(0, 0, spent), cancellationToken);
    }

    private async Task RecordAsync(TenantId tenantId, TokenUsage usage, CancellationToken cancellationToken)
    {
        try
        {
            await ledger.AddAsync(tenantId, UsagePeriod.Of(timeProvider.GetUtcNow()), usage, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRecordFailed(logger, tenantId.Value, usage.Total, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record {Tokens} tokens of usage for tenant {TenantId}; the budget is under-counted by that amount")]
    private static partial void LogRecordFailed(ILogger logger, Guid tenantId, long tokens, Exception exception);
}
