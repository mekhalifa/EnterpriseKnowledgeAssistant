using System.Reflection;
using EKA.Contracts;
using EKA.Infrastructure;

namespace EKA.IntegrationTests;

/// <summary>
/// Cross-cutting guards for the dependency rule in
/// docs/adr/0002-clean-architecture-layering.md. Each layer's own test project
/// guards its own boundary; this project covers the two layers that have no
/// dedicated test project (Contracts, Infrastructure).
///
/// Caveat worth knowing: the C# compiler omits assembly references that compiled
/// code never actually uses, so while the projects are nearly empty these assertions
/// pass easily. They gain teeth as real code lands from Step 2 onward — which is
/// exactly when the boundaries start being tempting to cross.
/// </summary>
public sealed class ArchitectureTests
{
    private static IEnumerable<string> EkaReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("EKA.", StringComparison.Ordinal));

    [Fact]
    public void Contracts_depends_on_no_other_EKA_project()
    {
        // EKA.Contracts is the wire format, shared with the Angular client's
        // generated types in Step 13. A reference to any other project would drag
        // internal model changes into the public API surface.
        var references = EkaReferencesOf(typeof(ContractsAssemblyMarker).Assembly);

        Assert.Empty(references);
    }

    [Theory]
    [InlineData("EKA.AI")]
    [InlineData("EKA.Api")]
    public void Infrastructure_does_not_reference_a_sibling_or_outer_project(string forbidden)
    {
        var references = EkaReferencesOf(typeof(InfrastructureAssemblyMarker).Assembly);

        Assert.DoesNotContain(forbidden, references);
    }
}
