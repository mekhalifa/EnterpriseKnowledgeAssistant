using EKA.Api.Errors;
using EKA.Api.Filters;
using EKA.Api.Security;
using EKA.Application.Abstractions;
using EKA.Application.Retrieval.SearchChunks;
using EKA.Contracts.Retrieval;

namespace EKA.Api.Endpoints;

/// <summary>
/// <c>POST /api/search</c>: semantic search over the documents the caller may read.
/// </summary>
/// <remarks>
/// <para>
/// Retrieval on its own, without answer generation. Step 10 calls the same
/// <c>ChunkRetriever</c> from inside the chat flow. The endpoint stays useful after that: it
/// is how a person checks <i>why</i> an answer said what it did, and how Step 11's
/// evaluation measures retrieval separately from generation.
/// </para>
/// <para>
/// Same group-level authorization as every other route group (<see cref="TenantPolicy"/>),
/// and the caller's <c>AccessScope</c> comes from the principal, never the body.
/// </para>
/// </remarks>
internal static class RetrievalEndpoints
{
    public static IEndpointRouteBuilder MapRetrievalEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var search = app.MapGroup("/api/search")
            .WithTags("Retrieval")
            .RequireAuthorization(TenantPolicy.Name);

        search.MapPost("/", SearchAsync)
            .WithName("SearchChunks")
            .RequireRateLimiting(RateLimitingOptions.SearchPolicy)
            .WithSummary("Finds the chunks the caller may read that best match a question.")
            .WithValidation<SearchRequest>()
            .Produces<SearchResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status502BadGateway);

        return app;
    }

    private static async Task<IResult> SearchAsync(
        SearchRequest request,
        SearchChunksHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var query = new SearchChunksQuery(currentUser.RequireAccessScope(), request.Query, request.TopK);

        var result = await handler.HandleAsync(query, cancellationToken).ConfigureAwait(false);

        return result.Match(Results.Ok);
    }
}
