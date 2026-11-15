using Microsoft.Extensions.Options;

namespace EKA.Application.Conversations;

/// <summary>
/// The <c>Chat</c> configuration section: which prompt template answers questions, and how
/// much of the context window a prompt may use.
/// </summary>
/// <remarks>
/// Tuning, not identity, so it has defaults (compare <c>AiOptions</c>, which has none for
/// model names). Bound in <c>EKA.Api</c>; this layer only reads it.
/// </remarks>
public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>Name of the <c>PromptTemplate</c> whose active version builds every prompt.</summary>
    public string PromptTemplateName { get; set; } = "default";

    /// <summary>
    /// Upper bound on the estimated prompt size: system instruction, history and question
    /// together. Deliberately far below the model's window (128k for gpt-4o-mini). That
    /// leaves headroom for estimation error and for the answer, and it caps what one
    /// question can cost. A long conversation is trimmed to fit this, not to fit the model.
    /// </summary>
    public int MaxPromptTokens { get; set; } = 6000;

    /// <summary>
    /// Upper bound on history messages, applied before the token budget. Even a cheap
    /// prompt gets worse as unrelated old turns pile up, and the model starts answering the
    /// conversation instead of the question.
    /// </summary>
    public int MaxHistoryMessages { get; set; } = 20;
}

internal sealed class ChatOptionsValidator : IValidateOptions<ChatOptions>
{
    public ValidateOptionsResult Validate(string? name, ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var section = ChatOptions.SectionName;

        if (string.IsNullOrWhiteSpace(options.PromptTemplateName))
        {
            failures.Add($"{section}:PromptTemplateName is required.");
        }

        // Below a few hundred tokens a system instruction alone does not fit, and every
        // question would be rejected as too long. That is a configuration error, not
        // something a user should discover by asking.
        if (options.MaxPromptTokens < 256)
        {
            failures.Add($"{section}:MaxPromptTokens must be at least 256 (got {options.MaxPromptTokens}).");
        }

        if (options.MaxHistoryMessages < 0)
        {
            failures.Add($"{section}:MaxHistoryMessages cannot be negative.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
