using EKA.Api.Errors;
using EKA.Api.Security;
using EKA.Application.Abstractions;
using EKA.Application.Usage;
using EKA.Contracts.Usage;

namespace EKA.Api.Endpoints;

/// <summary><c>GET /api/usage</c>: this month's token usage against the tenant's budget (Step 12).</summary>
internal static class UsageEndpoints
{
    public static IEndpointRouteBuilder MapUsageEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGroup("/api/usage")
            .WithTags("Usage")
            .RequireAuthorization(TenantPolicy.Name)
            .MapGet("/", GetAsync)
            .WithName("GetUsage")
            .WithSummary("This month's token usage and budget for the caller's tenant. Administrators only.")
            .Produces<UsageResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> GetAsync(GetUsageHandler handler, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetUsageQuery(currentUser.RequireAccessScope()), cancellationToken).ConfigureAwait(false);

        return result.Match(Results.Ok);
    }
}
