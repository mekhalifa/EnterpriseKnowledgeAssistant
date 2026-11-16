using EKA.Application.Abstractions.Usage;
using EKA.Domain.Common;
using EKA.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EKA.IntegrationTests.Persistence;

/// <summary>The usage ledger against real SQL Server: the budget is only as good as its arithmetic (Step 12).</summary>
[Collection(SqlServerCollection.Name)]
public sealed class TokenUsageLedgerTests(SqlServerFixture fixture)
{
    private ITokenUsageLedger Ledger()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:EkaDatabase"] = fixture.ConnectionString })
            .Build();

        return new ServiceCollection().AddLogging().AddInfrastructure(configuration)
            .BuildServiceProvider().GetRequiredService<ITokenUsageLedger>();
    }

    [Fact]
    public async Task Concurrent_increments_including_the_first_of_the_month_are_never_lost()
    {
        // Fifty answers finishing at once, the first of them creating the month's row. A
        // read-then-write would lose increments, and a MERGE without HOLDLOCK would let two
        // "first" requests both insert and one fail on the primary key.
        var ledger = Ledger();
        var tenant = new TenantId(Guid.NewGuid());
        var period = new UsagePeriod(2026, 11);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => ledger.AddAsync(tenant, period, new TokenUsage(10, 3, 1))));

        (await ledger.GetAsync(tenant, period)).ShouldBe(new TokenUsage(500, 150, 50));
    }

    [Fact]
    public async Task Tenants_and_months_are_kept_apart()
    {
        var ledger = Ledger();
        var tenant = new TenantId(Guid.NewGuid());
        var other = new TenantId(Guid.NewGuid());

        await ledger.AddAsync(tenant, new UsagePeriod(2026, 10), new TokenUsage(100, 0, 0));
        await ledger.AddAsync(tenant, new UsagePeriod(2026, 11), new TokenUsage(5, 0, 0));
        await ledger.AddAsync(other, new UsagePeriod(2026, 11), new TokenUsage(7, 0, 0));

        (await ledger.GetAsync(tenant, new UsagePeriod(2026, 11))).Total.ShouldBe(5);
        (await ledger.GetAsync(other, new UsagePeriod(2026, 11))).Total.ShouldBe(7);
        (await ledger.GetAsync(tenant, new UsagePeriod(2026, 12))).ShouldBe(TokenUsage.None);
    }
}
