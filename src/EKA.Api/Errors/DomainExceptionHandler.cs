using EKA.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace EKA.Api.Errors;

/// <summary>
/// Converts the two domain exception types into HTTP responses. Everything else is left to
/// the default handler, which produces a 500.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an exception handler exists at all in a codebase that prefers <c>Result</c>.</b>
/// Because one rule genuinely belongs to the domain and nowhere else: which lifecycle
/// transitions are legal. A handler that pre-checks the status before calling
/// <c>Document.Reset</c> has copied the state machine, and the copy will drift. Letting the
/// aggregate throw and mapping the throw once keeps a single authority. See ADR-0011.
/// </para>
/// <para>
/// <b>The two types mean different things, so they map differently.</b>
/// </para>
/// <list type="table">
/// <item>
/// <term><see cref="InvalidStateTransitionException"/></term>
/// <description>
/// <c>409 Conflict</c>. A legitimate request that arrived at the wrong moment — a retry
/// racing the original, a client acting on a document it read a minute ago. The caller
/// should re-read and decide, so the detail names the actual transition that was refused.
/// </description>
/// </item>
/// <item>
/// <term><see cref="DomainException"/></term>
/// <description>
/// <c>500</c>. An invariant broke, which means something upstream failed to validate and
/// handed the domain nonsense. That is our bug, not the caller's.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>Why the 500 hides its message and the 409 shows it.</b> A 4xx detail is addressed to
/// the caller and must be actionable; a 5xx detail is addressed to us and belongs in the
/// log, where it is written in full along with the stack. Echoing an internal exception
/// message to a client is how table names, file paths and occasionally another tenant's
/// identifiers end up in a browser console.
/// </para>
/// </remarks>
internal sealed class DomainExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not DomainException domainException)
        {
            // Not ours. Returning false lets the pipeline fall through to the default
            // handler and produce an opaque 500 — which is the correct response to an
            // exception nobody anticipated.
            return false;
        }

        var isTransition = domainException is InvalidStateTransitionException;

        if (isTransition)
        {
            logger.LogInformation(
                domainException,
                "Rejected an illegal state transition on {Path}.",
                httpContext.Request.Path);
        }
        else
        {
            logger.LogError(
                domainException,
                "A domain invariant was violated on {Path}. Something upstream failed to "
                + "validate before reaching the domain.",
                httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = isTransition
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = domainException,
            ProblemDetails =
            {
                Status = httpContext.Response.StatusCode,
                Title = isTransition ? "Conflict" : "Server error",
                Detail = isTransition
                    ? domainException.Message
                    : "The request could not be completed.",
                Extensions =
                {
                    [ErrorResults.CodeExtension] = isTransition
                        ? "domain.invalid_state_transition"
                        : "domain.invariant_violated",
                },
            },
        }).ConfigureAwait(false);
    }
}
