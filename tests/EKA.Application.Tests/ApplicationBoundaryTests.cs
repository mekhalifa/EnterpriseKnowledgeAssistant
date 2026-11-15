using System.Reflection;
using EKA.Application;

namespace EKA.Application.Tests;

/// <summary>
/// The application layer declares the interfaces it needs and knows nothing about who
/// implements them. These tests guard that from inside the boundary: this project
/// references only EKA.Application, so it cannot accidentally observe an outer layer.
/// </summary>
public sealed class ApplicationBoundaryTests
{
    private static readonly Assembly Application = typeof(ApplicationAssemblyMarker).Assembly;

    [Theory]
    [InlineData("EKA.Infrastructure")]
    [InlineData("EKA.AI")]
    [InlineData("EKA.Api")]
    public void Application_does_not_reference_an_outer_layer(string forbidden)
    {
        // If this fails, someone ran `dotnet add reference` in the wrong direction.
        // The fix is an interface in EKA.Application and an implementation in the
        // outer project, wired up in EKA.Api's composition root.
        var references = Application.GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(forbidden, references);
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Qdrant")]
    [InlineData("Hangfire")]
    // Step 5. Matching is by prefix, so "OpenAI" does not catch "Azure.AI.OpenAI" and the
    // SDK's client pipeline (System.ClientModel) — each is listed. Polly or the HTTP
    // resilience package here would mean retry policy had leaked into use cases.
    // Microsoft.Extensions.AI is vendor-neutral, and still forbidden: see ADR-0016.
    [InlineData("OpenAI")]
    [InlineData("Azure.AI")]
    [InlineData("System.ClientModel")]
    [InlineData("Microsoft.Extensions.AI")]
    [InlineData("Microsoft.Extensions.Http")]
    [InlineData("Polly")]
    public void Application_does_not_reference_an_infrastructure_package(string forbiddenPrefix)
    {
        var references = Application.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase));

        Assert.Empty(references);
    }
}
