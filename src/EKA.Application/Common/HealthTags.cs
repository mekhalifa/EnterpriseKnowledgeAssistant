namespace EKA.Application.Common;

/// <summary>
/// Tags that group health checks by which probe runs them.
/// </summary>
/// <remarks>
/// Here, rather than in <c>EKA.Infrastructure</c> or <c>EKA.Api</c>, because both of them
/// need the same string and neither may reference the other: infrastructure registers the
/// checks, the API decides which probe runs them. Two private copies would compile
/// perfectly and produce a readiness probe that filters on a tag nothing carries — a check
/// that never runs, reporting "Healthy" forever.
/// </remarks>
public static class HealthTags
{
    /// <summary>A dependency the instance cannot serve traffic without.</summary>
    public const string Ready = "ready";
}
