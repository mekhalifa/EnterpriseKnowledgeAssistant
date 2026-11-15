using EKA.Application.Common;
using EKA.Domain.Common;
using EKA.Domain.Conversations;

namespace EKA.Application.Abstractions.AI;

/// <summary>
/// Asks a language model to continue a conversation. The application's port to whatever
/// model backend is configured.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this interface exists when <c>Microsoft.Extensions.AI.IChatClient</c> already
/// does.</b> <c>IChatClient</c> is vendor-neutral, but it is not <i>ours</i>: it speaks in
/// <c>ChatMessage</c>, <c>ChatRole</c> and <c>UsageDetails</c>, and it reports failure by
/// throwing whatever the underlying SDK throws. Letting it into use cases would make every
/// handler translate SDK exceptions and every test reference an AI package. This port speaks
/// in domain vocabulary (<see cref="MessageRole"/>, <see cref="TokenCount"/>) and returns a
/// <see cref="Result{TValue}"/>, so an upstream outage is an expected failure the handler
/// passes on as a 502 — not an exception it has to know to catch. See ADR-0016.
/// </para>
/// <para>
/// <b>Failure contract.</b> Provider failures come back as an <see cref="Error"/> of type
/// <see cref="ErrorType.Provider"/>, with a code from <see cref="AiErrors"/>. Cancellation by
/// the caller is <i>not</i> a failure and still throws <see cref="OperationCanceledException"/>:
/// the caller asked to stop, and there is nobody left to send a 502 to.
/// </para>
/// </remarks>
public interface IChatCompletionService
{
    Task<Result<ChatCompletion>> CompleteAsync(
        IReadOnlyList<ChatTurn> conversation,
        CancellationToken cancellationToken = default);
}

/// <summary>One message handed to the model.</summary>
/// <remarks>
/// Reuses the domain's <see cref="MessageRole"/> deliberately: it is a trust boundary
/// (document text is never <see cref="MessageRole.System"/>), and a second role enum here
/// would be a second place to get that wrong.
/// </remarks>
public sealed record ChatTurn(MessageRole Role, string Content);

/// <summary>What the model answered, and what it cost.</summary>
/// <param name="Content">The answer text.</param>
/// <param name="Model">The model that actually answered, as reported by the provider — a
/// gateway can route to a different revision than the one requested.</param>
/// <param name="InputTokens">Prompt tokens, or null if the provider did not report usage.
/// Null rather than zero: "free" and "unknown" are different facts for a cost report.</param>
/// <param name="OutputTokens">Completion tokens, or null if unreported.</param>
public sealed record ChatCompletion(
    string Content,
    string? Model,
    TokenCount? InputTokens,
    TokenCount? OutputTokens);
