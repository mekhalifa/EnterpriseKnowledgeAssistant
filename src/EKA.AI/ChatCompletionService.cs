using EKA.Application.Abstractions.AI;
using EKA.Application.Common;
using EKA.Domain.Common;
using EKA.Domain.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace EKA.AI;

/// <summary>
/// Adapter from the application's <see cref="IChatCompletionService"/> to
/// <c>Microsoft.Extensions.AI</c>'s <see cref="IChatClient"/>.
/// </summary>
/// <remarks>
/// It knows nothing about OpenAI, OpenRouter or HTTP. The <see cref="IChatClient"/> it
/// receives is already the full pipeline — logging middleware wrapping the OpenAI adapter
/// wrapping an <see cref="HttpClient"/> with timeout, retry and circuit breaker — assembled
/// in <see cref="DependencyInjection"/>. This class only translates vocabulary in both
/// directions.
/// </remarks>
internal sealed partial class ChatCompletionService(
    IChatClient chatClient,
    ILogger<ChatCompletionService> logger) : IChatCompletionService
{
    public async Task<Result<ChatCompletion>> CompleteAsync(
        IReadOnlyList<ChatTurn> conversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (conversation.Count == 0)
        {
            throw new ArgumentException("A conversation needs at least one message.", nameof(conversation));
        }

        var messages = conversation.Select(ToChatMessage).ToList();

        ChatResponse response;
        try
        {
            response = await chatClient
                .GetResponseAsync(messages, options: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (ProviderFailure.TryTranslate(exception, cancellationToken, out var error))
        {
            LogProviderFailure(logger, error.Code, exception);
            return error;
        }

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            // A 200 with no content: content filtering, a model that only produced a tool
            // call, or a gateway hiccup. Passing "" on to Step 6 would persist a blank answer.
            LogEmptyResponse(logger, response.ModelId);
            return AiErrors.EmptyResponse;
        }

        return new ChatCompletion(
            text,
            response.ModelId,
            Tokens.From(response.Usage?.InputTokenCount),
            Tokens.From(response.Usage?.OutputTokenCount));
    }

    private static ChatMessage ToChatMessage(ChatTurn turn) => new(
        turn.Role switch
        {
            MessageRole.System => ChatRole.System,
            MessageRole.User => ChatRole.User,
            MessageRole.Assistant => ChatRole.Assistant,
            // A new role is a decision about trust (see MessageRole), not something to
            // default. Fail loudly until someone has made it.
            _ => throw new ArgumentOutOfRangeException(nameof(turn), turn.Role, "Unmapped message role."),
        },
        turn.Content);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chat completion failed with {ErrorCode}")]
    private static partial void LogProviderFailure(ILogger logger, string errorCode, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chat completion from {Model} returned no text")]
    private static partial void LogEmptyResponse(ILogger logger, string? model);
}

internal static class Tokens
{
    /// <summary>
    /// Provider usage is a nullable <see cref="long"/>; <see cref="TokenCount"/> is a
    /// non-negative <see cref="int"/>. Null stays null ("not reported"), and a value that
    /// does not fit is clamped rather than thrown on — losing precision on an absurd usage
    /// figure is better than failing a call that already succeeded and was paid for.
    /// </summary>
    public static TokenCount? From(long? count) => count switch
    {
        null => null,
        < 0 => null,
        > int.MaxValue => new TokenCount(int.MaxValue),
        _ => new TokenCount((int)count.Value),
    };
}
