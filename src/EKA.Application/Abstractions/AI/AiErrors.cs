using EKA.Application.Common;

namespace EKA.Application.Abstractions.AI;

/// <summary>
/// The provider failures a use case can receive from <see cref="IChatCompletionService"/>
/// or <see cref="IEmbeddingService"/>. All map to HTTP 502.
/// </summary>
/// <remarks>
/// <para>
/// Declared here, not in <c>EKA.AI</c>, because the codes are part of the API contract and a
/// handler may branch on them — Step 10 may, for example, answer "the assistant is busy" on
/// <see cref="RateLimited"/> but not on <see cref="AuthenticationFailed"/>.
/// </para>
/// <para>
/// <b>Messages are deliberately vague.</b> They reach the client. The upstream status,
/// response body and request id go to the log, where an operator can see them; a client
/// learning which vendor we use and how it rejected our key is an information leak.
/// </para>
/// </remarks>
public static class AiErrors
{
    public static readonly Error Timeout = Error.Provider(
        "ai.timeout",
        "The AI provider did not respond in time.");

    public static readonly Error Unavailable = Error.Provider(
        "ai.unavailable",
        "The AI provider could not be reached.");

    /// <summary>The circuit breaker is open: recent calls failed, so this one was not sent.</summary>
    public static readonly Error CircuitOpen = Error.Provider(
        "ai.circuit_open",
        "The AI provider is failing; requests are paused briefly.");

    public static readonly Error RateLimited = Error.Provider(
        "ai.rate_limited",
        "The AI provider is rate limiting requests.");

    /// <summary>
    /// 401/403 from upstream. Our credential is wrong — which is our configuration fault, but
    /// still a 502 to the client: from their side, a dependency failed.
    /// </summary>
    public static readonly Error AuthenticationFailed = Error.Provider(
        "ai.authentication_failed",
        "The AI provider rejected the service's credentials.");

    public static readonly Error Rejected = Error.Provider(
        "ai.rejected",
        "The AI provider rejected the request.");

    public static readonly Error DimensionMismatch = Error.Provider(
        "ai.embedding_dimension_mismatch",
        "The embedding model returned vectors of an unexpected size.");

    public static readonly Error EmptyResponse = Error.Provider(
        "ai.empty_response",
        "The AI provider returned no content.");
}
