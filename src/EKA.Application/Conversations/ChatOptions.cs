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

    /// <summary>
    /// Upper bound on the sources section: the retrieved chunks the model is shown (Step 10).
    /// 3,000 holds the default five chunks of about 500 tokens with room to spare. It is spent
    /// before history, inside <see cref="MaxPromptTokens"/>.
    /// </summary>
    public int MaxContextTokens { get; set; } = 3000;

    /// <summary>
    /// The answer when retrieval finds nothing relevant that the caller may read. Given by the
    /// system, without a model call.
    /// </summary>
    /// <remarks>
    /// <b>The same words whether nothing exists or the caller may not see it.</b> "There is a
    /// document but you lack access" would confirm that a restricted document covers the
    /// question, which is exactly the kind of fact access control exists to protect.
    /// <para>
    /// <b>It also says what the assistant is for</b> (2026-11-18). A question about the assistant
    /// itself ("what can you help with?") matches no document and lands here, so this is where a
    /// new user learns how to use it. Chosen over detecting such questions before retrieval,
    /// which would skip the search for any real question it misclassified.
    /// </para>
    /// </remarks>
    public string NoEvidenceAnswer { get; set; } =
        "I couldn't find anything in the documents available to you that answers this. "
        + "I answer questions from your organization's documents, such as policies and procedures, "
        + "and show the source of every answer. Try asking about a specific topic, or rephrase your question.";
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

        // The sources live inside the prompt budget. A cap at or above it would let sources
        // crowd out the instruction and question, and every grounded question would fail.
        if (options.MaxContextTokens < 1 || options.MaxContextTokens >= options.MaxPromptTokens)
        {
            failures.Add($"{section}:MaxContextTokens must be at least 1 and below MaxPromptTokens ({options.MaxPromptTokens}); got {options.MaxContextTokens}.");
        }

        if (string.IsNullOrWhiteSpace(options.NoEvidenceAnswer))
        {
            failures.Add($"{section}:NoEvidenceAnswer is required.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
