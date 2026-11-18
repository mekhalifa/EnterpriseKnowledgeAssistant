using EKA.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;

namespace EKA.Api.Errors;

/// <summary>
/// Answers <c>503 Service Unavailable</c> when a request failed because the database could
/// not be reached.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not leave it a 500.</b> A 500 says "our bug; retrying will not help". During a
/// database outage that is false on both counts: nothing is wrong with the request or the
/// code, and the same request succeeds once the database is back. The Step 14 drill stopped
/// SQL Server and saw reads answer 500. 503 is the status that says "temporary", which is what
/// clients, proxies and the people reading dashboards act on.
/// </para>
/// <para>
/// <b>Why an exception handler, not a <c>Result</c>.</b> An unreachable database is not an
/// expected outcome of any use case (ADR-0011), and threading it through every repository
/// method would add a failure branch to code that cannot do anything about it. It is mapped
/// once, at the edge, like the domain's two exceptions.
/// </para>
/// <para>
/// <b>Logged at Warning, not Error.</b> During an outage every request fails the same way; the
/// incident is already visible on the readiness probe. Error stays reserved for failures that
/// point at our code. The exception is still logged in full, with its trace id.
/// </para>
/// </remarks>
internal sealed partial class DatabaseUnavailableExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<DatabaseUnavailableExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>The problem code a client switches on.</summary>
    public const string Code = "database.unavailable";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (!SqlServerFailures.IsUnavailable(exception))
        {
            return false;
        }

        LogUnavailable(logger, httpContext.Request.Path, exception);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Service unavailable",
                // Says what the caller can do. The connection error itself, which names hosts
                // and ports, stays in the log.
                Detail = "The service is temporarily unavailable. Try again in a moment.",
                Extensions = { [ErrorResults.CodeExtension] = Code },
            },
        }).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The database could not be reached while serving {Path}; answered 503.")]
    private static partial void LogUnavailable(ILogger logger, PathString path, Exception exception);
}
