using System.Text.RegularExpressions;
using EKA.Application.Abstractions.AI;
using EKA.Application.Common;
using EKA.Domain.Common;
using EKA.Domain.Conversations;
using EKA.Domain.Prompting;

namespace EKA.Application.Conversations;

/// <summary>
/// Assembles what is sent to the model: template, history and question, fitted to a token
/// budget. The Builder from docs/concepts/design-patterns.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a Builder rather than one method with five parameters.</b> The parts arrive from
/// different places (template from the database, history from the aggregate, budget from
/// configuration) and Step 10 adds a sixth: retrieved context. Each <c>With…</c> call names
/// one part. <see cref="Build"/> is the single place that knows the order of the parts and
/// the trimming rule, so neither can drift between callers.
/// </para>
/// <para>
/// <b>Output order:</b> one <see cref="MessageRole.System"/> turn (the template's
/// instruction, and nothing else, ever), then history oldest to newest, then the question
/// rendered through the template as a <see cref="MessageRole.User"/> turn.
/// </para>
/// <para>
/// <b>Trimming rule:</b> the system turn and the question are mandatory. If they alone
/// exceed the budget the question is rejected. History is added newest first while it fits,
/// up to the message cap. If the oldest kept message is an answer, it is dropped: an answer
/// whose question was trimmed reads to the model as an unprompted assertion.
/// </para>
/// </remarks>
public sealed partial class PromptBuilder(ITokenCounter tokenCounter)
{
    /// <summary>
    /// Approximate per-message framing cost of the chat format (role markers, separators).
    /// Small, but over twenty history messages it is 80 tokens the estimate would otherwise
    /// miss.
    /// </summary>
    public const int PerMessageOverhead = 4;

    private PromptTemplate? _template;
    private IReadOnlyCollection<Message> _history = [];
    private string? _question;
    private int _maxPromptTokens;
    private int _maxHistoryMessages;

    public PromptBuilder WithTemplate(PromptTemplate template)
    {
        _template = template ?? throw new ArgumentNullException(nameof(template));
        return this;
    }

    /// <param name="history">Prior messages in conversation order, oldest first.</param>
    public PromptBuilder WithHistory(IReadOnlyCollection<Message> history)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        return this;
    }

    public PromptBuilder WithQuestion(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        _question = question;
        return this;
    }

    public PromptBuilder WithBudget(int maxPromptTokens, int maxHistoryMessages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPromptTokens, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxHistoryMessages);
        _maxPromptTokens = maxPromptTokens;
        _maxHistoryMessages = maxHistoryMessages;
        return this;
    }

    public Result<AssembledPrompt> Build()
    {
        var template = _template ?? throw new InvalidOperationException("WithTemplate was not called.");
        var question = _question ?? throw new InvalidOperationException("WithQuestion was not called.");

        if (_maxPromptTokens == 0)
        {
            throw new InvalidOperationException("WithBudget was not called.");
        }

        var system = new ChatTurn(MessageRole.System, template.SystemPrompt);
        var asked = new ChatTurn(MessageRole.User, Render(template.UserPromptTemplate, question, context: string.Empty));

        var used = Cost(system.Content) + Cost(asked.Content);

        if (used > _maxPromptTokens)
        {
            return ConversationErrors.QuestionTooLong(_maxPromptTokens);
        }

        // Newest first, so trimming removes the oldest turns: they are the least likely to
        // matter to the question being asked now.
        var kept = new List<Message>();
        foreach (var message in _history.Reverse())
        {
            if (kept.Count == _maxHistoryMessages)
            {
                break;
            }

            var cost = Cost(message);
            if (used + cost > _maxPromptTokens)
            {
                break;
            }

            kept.Add(message);
            used += cost;
        }

        kept.Reverse();

        if (kept.Count > 0 && kept[0].Role is MessageRole.Assistant)
        {
            used -= Cost(kept[0]);
            kept.RemoveAt(0);
        }

        var turns = new List<ChatTurn>(kept.Count + 2) { system };
        turns.AddRange(kept.Select(message => new ChatTurn(message.Role, message.Content)));
        turns.Add(asked);

        return new AssembledPrompt(
            turns,
            new TokenCount(used),
            HistoryMessagesIncluded: kept.Count,
            HistoryMessagesOmitted: _history.Count - kept.Count,
            template.Version);
    }

    /// <summary>
    /// Single-pass placeholder substitution.
    /// </summary>
    /// <remarks>
    /// Two <c>string.Replace</c> calls in a row would rescan text inserted by the first. A
    /// question containing <c>{{context}}</c> would then have the retrieved documents
    /// substituted <i>into the user's own words</i>. From Step 10 on, a document containing
    /// <c>{{question}}</c> would likewise be rewritten. One regex pass over the template
    /// replaces only placeholders that were in the template: inserted values are never
    /// rescanned.
    /// </remarks>
    internal static string Render(string userPromptTemplate, string question, string context) =>
        PlaceholderPattern().Replace(
            userPromptTemplate,
            match => match.Value == PromptTemplate.QuestionPlaceholder ? question : context);

    private int Cost(string text) => tokenCounter.Count(text).Value + PerMessageOverhead;

    /// <summary>
    /// The provider's exact count when the message has one (assistant output), otherwise
    /// the estimate. User messages carry none by domain design: they are not billed
    /// separately.
    /// </summary>
    private int Cost(Message message) =>
        (message.TokenCount ?? tokenCounter.Count(message.Content)).Value + PerMessageOverhead;

    [GeneratedRegex(@"\{\{(?:question|context)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}

/// <summary>The prompt as sent, plus what was decided while building it.</summary>
/// <param name="Turns">System first, history, question last.</param>
/// <param name="EstimatedTokens">The builder's estimate. The provider's own count comes
/// back on the completion.</param>
/// <param name="HistoryMessagesIncluded">How many prior messages were sent.</param>
/// <param name="HistoryMessagesOmitted">How many were trimmed for budget or cap. Non-zero
/// means the model did not see the whole conversation, which is worth surfacing.</param>
/// <param name="TemplateVersion">The prompt template version that produced the prompt.</param>
public sealed record AssembledPrompt(
    IReadOnlyList<ChatTurn> Turns,
    TokenCount EstimatedTokens,
    int HistoryMessagesIncluded,
    int HistoryMessagesOmitted,
    int TemplateVersion);
