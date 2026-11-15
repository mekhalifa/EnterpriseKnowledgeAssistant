using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using EKA.Application.Abstractions.AI;
using EKA.Application.Common;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace EKA.AI;

/// <summary>
/// Translates what the provider stack throws into the <see cref="AiErrors"/> the
/// application understands. The one place that knows the SDK's exception vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the inner-exception walk.</b> Failures arrive wrapped, and the wrapping depends on
/// where they happened. The OpenAI SDK's HTTP transport wraps an
/// <see cref="HttpRequestException"/> in a <see cref="ClientResultException"/> with status 0;
/// the resilience handler's own exceptions (<see cref="TimeoutRejectedException"/>,
/// <see cref="BrokenCircuitException"/>) may or may not be wrapped depending on the SDK
/// version. Walking the chain and taking the first <i>informative</i> exception makes the
/// translation independent of those details.
/// </para>
/// <para>
/// <b>What deliberately is not translated.</b> Anything unrecognized — an
/// <see cref="ArgumentException"/>, a <see cref="NullReferenceException"/>, a JSON parse
/// failure — propagates and becomes a 500. Those are bugs, on our side or a malformed
/// response, and dressing a bug up as "the provider is down" sends someone to debug the
/// wrong system.
/// </para>
/// </remarks>
internal static class ProviderFailure
{
    public static bool TryTranslate(
        Exception exception,
        CancellationToken callerToken,
        [NotNullWhen(true)] out Error? error)
    {
        // The caller gave up. Not a provider failure, and nobody is waiting for a 502:
        // let the cancellation propagate.
        if (exception is OperationCanceledException && callerToken.IsCancellationRequested)
        {
            error = null;
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            error = current switch
            {
                TimeoutRejectedException => AiErrors.Timeout,
                BrokenCircuitException => AiErrors.CircuitOpen,
                ClientResultException { Status: 401 or 403 } => AiErrors.AuthenticationFailed,
                ClientResultException { Status: 429 } => AiErrors.RateLimited,
                ClientResultException { Status: 408 or 504 } => AiErrors.Timeout,
                ClientResultException { Status: >= 500 } => AiErrors.Unavailable,
                ClientResultException { Status: >= 400 } => AiErrors.Rejected,
                HttpRequestException => AiErrors.Unavailable,
                // Cancellation the caller did not ask for is a timeout somewhere below us.
                OperationCanceledException => AiErrors.Timeout,
                _ => null,
            };

            if (error is not null)
            {
                return true;
            }
        }

        error = null;
        return false;
    }
}
