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

    /// <summary>
    /// The same call, with the answer's text handed to <paramref name="onDelta"/> piece by piece
    /// as the model writes it (Step 13, ADR-0033).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the result is still a whole <see cref="ChatCompletion"/>.</b> Streaming changes when
    /// the user <i>sees</i> text, not what the application does with it: citations can only be
    /// extracted, validated and renumbered from the finished answer (step-10), the budget needs
    /// the usage that arrives last, and a turn is persisted whole or not at all. So the deltas are
    /// a side channel for display, and the return value is the same contract as
    /// <see cref="CompleteAsync"/> — including failure: a provider that dies half-way returns an
    /// error, and whatever was already streamed is discarded by the caller.
    /// </para>
    /// <para>
    /// The concatenated deltas are the raw model output; they are not guaranteed to equal the
    /// returned <see cref="ChatCompletion.Content"/> after an adapter's own trimming, and a
    /// caller must not persist them.
    /// </para>
    /// <para>
    /// The default implementation is for an adapter that cannot stream: it completes normally
    /// and reports the whole answer as one delta. Correct, merely not incremental.
    /// </para>
    /// </remarks>
    async Task<Result<ChatCompletion>> StreamAsync(
        IReadOnlyList<ChatTurn> conversation,
        Func<string, CancellationToken, ValueTask> onDelta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onDelta);

        var completion = await CompleteAsync(conversation, cancellationToken).ConfigureAwait(false);
        if (completion.IsSuccess)
        {
            await onDelta(completion.Value.Content, cancellationToken).ConfigureAwait(false);
        }

        return completion;
    }
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
/// <param name="Provider">Which service answered, e.g. <c>openrouter.ai</c>. The domain
/// requires it on every assistant message.</param>
/// <param name="Model">The model that actually answered, as reported by the provider — a
/// gateway can route to a different revision than the one requested. Falls back to the
/// configured model only if the provider reports none.</param>
/// <param name="InputTokens">Prompt tokens, or null if the provider did not report usage.
/// Null rather than zero: "free" and "unknown" are different facts for a cost report.</param>
/// <param name="OutputTokens">Completion tokens, or null if unreported.</param>
public sealed record ChatCompletion(
    string Content,
    string Provider,
    string Model,
    TokenCount? InputTokens,
    TokenCount? OutputTokens);
