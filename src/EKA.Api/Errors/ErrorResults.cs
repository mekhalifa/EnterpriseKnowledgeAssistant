using EKA.Application.Common;

namespace EKA.Api.Errors;

/// <summary>
/// Turns an application-layer <see cref="Error"/> into an HTTP response. The only place
/// that mapping exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one place.</b> Six handlers each deciding that "not found" means 404 is six
/// chances for one of them to answer 400, and nothing fails when it does — the test asserts
/// what the code does. Collected here, the contract is one switch a reviewer can read in
/// full, and a new <see cref="ErrorType"/> cannot be added without the compiler asking what
/// status it returns.
/// </para>
/// <para>
/// <b>Why RFC 9457 ProblemDetails.</b> It is the standard shape for an HTTP error — a
/// <c>type</c>, <c>title</c>, <c>status</c> and <c>detail</c> — so clients, proxies and
/// logging tools already understand it. The alternative is a bespoke error envelope that
/// every consumer has to be taught.
/// </para>
/// <para>
/// The application's own <see cref="Error.Code"/> rides along as a ProblemDetails
/// extension member, which the RFC explicitly allows. A client switching on
/// <c>document.not_found</c> gets a stable identifier; a client switching on the English in
/// <c>detail</c> gets a breaking change the next time someone improves a sentence.
/// </para>
/// </remarks>
internal static class ErrorResults
{
    /// <summary>Extension member carrying <see cref="Error.Code"/>.</summary>
    public const string CodeExtension = "code";

    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var (statusCode, title) = error.Type switch
        {
            ErrorType.Validation => (StatusCodes.Status400BadRequest, "Invalid request"),
            ErrorType.NotFound => (StatusCodes.Status404NotFound, "Not found"),
            ErrorType.Conflict => (StatusCodes.Status409Conflict, "Conflict"),
            ErrorType.Forbidden => (StatusCodes.Status403Forbidden, "Forbidden"),

            // 502, not 500. The fault is upstream, and the distinction tells a caller
            // whether retrying is sensible: a provider timeout usually is, our bug is not.
            ErrorType.Provider => (StatusCodes.Status502BadGateway, "Upstream provider failed"),

            // Not an expected branch — it means an ErrorType was added without deciding
            // what it returns. Throwing here surfaces that during the first request in
            // development rather than shipping an arbitrary status code.
            _ => throw new ArgumentOutOfRangeException(
                nameof(error),
                error.Type,
                "No HTTP status is mapped for this error type."),
        };

        return TypedResults.Problem(
            detail: error.Message,
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?>
            {
                [CodeExtension] = error.Code,
            });
    }

    /// <summary>
    /// Returns <paramref name="onSuccess"/> for a successful result, or the mapped problem
    /// response for a failed one.
    /// </summary>
    /// <remarks>
    /// Exists so an endpoint body stays the three lines it should be. Without it every
    /// endpoint repeats the same <c>if (result.IsFailure) return result.Error.ToProblem();</c>
    /// — harmless until the day one of them forgets and returns a 200 carrying a default
    /// value.
    /// </remarks>
    public static IResult Match<TValue>(this Result<TValue> result, Func<TValue, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        return result.IsSuccess ? onSuccess(result.Value) : result.Error.ToProblem();
    }

    /// <inheritdoc cref="Match{TValue}"/>
    public static IResult Match(this Result result, Func<IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        return result.IsSuccess ? onSuccess() : result.Error.ToProblem();
    }
}
