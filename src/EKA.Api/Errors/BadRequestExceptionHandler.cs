using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

namespace EKA.Api.Errors;

/// <summary>
/// Preserves the status code the framework had already chosen for a malformed request.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this exists to fix, because it is not obvious.</b> When a client posts
/// <c>{ this is not json</c>, minimal API parameter binding throws
/// <see cref="BadHttpRequestException"/>, which carries
/// <see cref="BadHttpRequestException.StatusCode"/> <c>400</c> — and without any exception
/// handler registered, ASP.NET Core honours it. Adding <c>app.UseExceptionHandler()</c>
/// changes that: the exception now reaches the handler pipeline, no handler claims it, and
/// the fallback writes <c>500</c>.
/// </para>
/// <para>
/// So switching on structured error handling silently converts a correct 400 into a 500
/// that blames the server for the client's malformed JSON — and it does so for every
/// endpoint at once, with nothing in the diff to suggest it. An end-to-end test caught it;
/// no unit test could have.
/// </para>
/// <para>
/// Registered before <see cref="DomainExceptionHandler"/> because handlers run in
/// registration order and the first one to return <c>true</c> wins. The two claim disjoint
/// exception types, so the order is documentation rather than a dependency — but relying on
/// that staying true is how ordering bugs get written.
/// </para>
/// </remarks>
internal sealed class BadRequestExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<BadRequestExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        logger.LogInformation(
            badRequest,
            "Rejected a malformed request on {Path}.",
            httpContext.Request.Path);

        httpContext.Response.StatusCode = badRequest.StatusCode;

        // The exception's own message names the parameter that failed to bind and carries
        // a JsonException underneath it. Useful in a log, not in a response: it describes
        // our method signatures to whoever sent the bad request.
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = badRequest,
            ProblemDetails =
            {
                Status = badRequest.StatusCode,
                Title = "Invalid request",
                Detail = "The request body could not be read.",
                Extensions =
                {
                    [ErrorResults.CodeExtension] = "request.malformed",
                },
            },
        }).ConfigureAwait(false);
    }
}
