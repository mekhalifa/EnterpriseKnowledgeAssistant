using System.Reflection;
using EKA.Domain;

namespace EKA.Domain.Tests;

/// <summary>
/// The domain layer's defining property is that it has no infrastructure. These tests
/// live in the Domain test project (which references only EKA.Domain) so the guarantee
/// is checked from inside the boundary it protects.
/// </summary>
public sealed class DomainIsolationTests
{
    private static readonly Assembly Domain = typeof(DomainAssemblyMarker).Assembly;

    [Fact]
    public void Domain_references_no_other_EKA_assembly()
    {
        var references = Domain.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("EKA.", StringComparison.Ordinal));

        Assert.Empty(references);
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.Extensions.AI")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Qdrant")]
    [InlineData("Azure")]
    [InlineData("Hangfire")]
    public void Domain_references_no_infrastructure_package(string forbiddenPrefix)
    {
        // Business rules must stay testable without a database, a model provider,
        // or a web host. This test is the thing that notices when that slips.
        var references = Domain.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase));

        Assert.Empty(references);
    }
}
