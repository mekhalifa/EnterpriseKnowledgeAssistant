using System.Reflection;
using EKA.AI;

namespace EKA.AI.Tests;

/// <summary>
/// EKA.AI is an adapter: it translates provider SDKs into the abstractions that
/// EKA.Application declares. It may depend inward, never sideways into the sibling
/// adapter (EKA.Infrastructure) and never outward into the host (EKA.Api).
/// </summary>
public sealed class AiBoundaryTests
{
    private static readonly Assembly Ai = typeof(AiAssemblyMarker).Assembly;

    [Theory]
    [InlineData("EKA.Infrastructure")]
    [InlineData("EKA.Api")]
    public void AI_does_not_reference_a_sibling_or_outer_project(string forbidden)
    {
        // Anything EKA.AI and EKA.Infrastructure both need belongs in
        // EKA.Application or EKA.Domain, not in a reference between them.
        var references = Ai.GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(forbidden, references);
    }
}
