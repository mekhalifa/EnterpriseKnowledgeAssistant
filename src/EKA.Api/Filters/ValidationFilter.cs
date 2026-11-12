using FluentValidation;

namespace EKA.Api.Filters;

/// <summary>
/// Runs the registered <see cref="IValidator{T}"/> against an endpoint's request body and
/// rejects an invalid one with <c>400</c> and a field-by-field error list.
/// </summary>
/// <typeparam name="TRequest">The request type to validate.</typeparam>
/// <remarks>
/// <para>
/// <b>Why a filter and not a line in every handler.</b> Validation written inside a handler
/// is validation that can be omitted from the next handler, and the omission looks exactly
/// like a handler that has nothing to validate. As a filter it is declared on the endpoint,
/// visible in the route definition, and runs before the handler is constructed — so an
/// invalid request never reaches a database connection.
/// </para>
/// <para>
/// <b>Why the response is <c>ValidationProblemDetails</c>.</b> It is RFC 9457 with an
/// <c>errors</c> object keyed by property name, which is the shape a form can bind directly
/// to its fields. A flat string — "Title must not be empty, ContentHash is invalid" — makes
/// every client write a parser.
/// </para>
/// <para>
/// <b>What this filter must never do.</b> Reach the database. "Has this hash been seen
/// before?" looks like validation and is not: it needs state, it runs before the handler's
/// transaction, and it would double the request's round trips while still racing a
/// concurrent insert. Rules that need state belong in the handler, returning a
/// <c>Result</c> failure. See ADR-0013.
/// </para>
/// </remarks>
internal sealed class ValidationFilter<TRequest>(IValidator<TRequest> validator) : IEndpointFilter
    where TRequest : class
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();

        if (request is null)
        {
            // Reached when the client posts a literal `null` body, or when this filter was
            // attached to an endpoint that takes no TRequest. Both are bad requests from
            // the caller's point of view, and both are better than silently validating
            // nothing.
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["body"] = ["A request body is required."],
            });
        }

        var result = await validator
            .ValidateAsync(request, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return result.IsValid
            ? await next(context).ConfigureAwait(false)
            : TypedResults.ValidationProblem(result.ToDictionary());
    }
}

/// <summary>Attaches <see cref="ValidationFilter{TRequest}"/> to an endpoint.</summary>
internal static class ValidationFilterExtensions
{
    /// <summary>
    /// Validates <typeparamref name="TRequest"/> before the handler runs, and declares the
    /// 400 response so it appears in the OpenAPI document.
    /// </summary>
    /// <remarks>
    /// The <c>Produces</c> call is not decoration. Step 13 generates the Angular client's
    /// types from this document; a 400 shape that is real but undeclared becomes a response
    /// the generated client has no type for, and the handling gets written by hand on the
    /// other side of the wire.
    /// </remarks>
    public static RouteHandlerBuilder WithValidation<TRequest>(this RouteHandlerBuilder builder)
        where TRequest : class =>
        builder
            .AddEndpointFilter<ValidationFilter<TRequest>>()
            .ProducesValidationProblem();
}
