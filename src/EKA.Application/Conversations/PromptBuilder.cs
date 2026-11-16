using System.Text.RegularExpressions;
using EKA.Application.Abstractions.AI;
using EKA.Application.Common;
using EKA.Application.Retrieval;
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
/// rendered through the template as a <see cref="MessageRole.User"/> turn, with the numbered
/// sources in its <c>{{context}}</c> section (Step 10).
/// </para>
/// <para>
/// <b>Trimming rule:</b> the system turn and the question are mandatory. If they alone
/// exceed the budget the question is rejected. Sources come next, best first, within their
/// own cap; a source that does not fit is skipped whole. History gets what is left. Evidence
/// outranks old turns, because a grounded answer without its evidence is a guess.
/// History is added newest first while it fits,
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
    private IReadOnlyList<RetrievedChunk> _sources = [];
    private string? _question;
    private int _maxPromptTokens;
    private int _maxHistoryMessages;
    private int _maxContextTokens;

    /// <summary>
    /// The retrieved chunks, best first, to offer the model as numbered sources (Step 10).
    /// </summary>
    /// <param name="sources">Ranked by the retriever. When the budget cannot hold them all, the
    /// lowest-ranked are left out, and only the sources included can be cited.</param>
    /// <param name="maxContextTokens">The most the sources section may cost. It is spent before
    /// history: for a grounded answer, evidence is worth more than old turns.</param>
    public PromptBuilder WithSources(IReadOnlyList<RetrievedChunk> sources, int maxContextTokens)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxContextTokens, 1);
        _maxContextTokens = maxContextTokens;
        return this;
    }

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

        if (_sources.Count > 0 && !template.SupportsGrounding)
        {
            // A template with nowhere to put the sources would send the question without them,
            // and the model would answer from its training data with no error anywhere.
            throw new InvalidOperationException(
                $"Prompt template '{template.Name}' v{template.Version} has no {PromptTemplate.ContextPlaceholder} placeholder, so it cannot carry sources.");
        }

        var system = new ChatTurn(MessageRole.System, template.SystemPrompt);

        var used = Cost(system.Content) + Cost(Render(template.UserPromptTemplate, question, context: string.Empty));

        if (used > _maxPromptTokens)
        {
            return ConversationErrors.QuestionTooLong(_maxPromptTokens);
        }

        // Sources next, best first, renumbered 1..n in the order they are kept. A source that
        // does not fit is skipped, not truncated: half a passage can say the opposite of the
        // whole one ("…unless approved in writing").
        var included = new List<PromptSource>();
        var contextUsed = 0;
        var sourcesOmitted = 0;
        foreach (var chunk in _sources)
        {
            var candidate = new PromptSource(included.Count + 1, chunk);
            var cost = tokenCounter.Count(SourceRenderer.Render(candidate) + "\n\n").Value;

            if (contextUsed + cost > _maxContextTokens || used + cost > _maxPromptTokens)
            {
                sourcesOmitted++;
                continue;
            }

            included.Add(candidate);
            contextUsed += cost;
            used += cost;
        }

        var asked = new ChatTurn(
            MessageRole.User,
            Render(template.UserPromptTemplate, question, SourceRenderer.RenderAll(included)));

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
        turns.AddRange(kept.Select(message => new ChatTurn(
            message.Role,
            // Earlier answers' [n] referred to that turn's sources, not this one's.
            message.Role is MessageRole.Assistant ? CitationMarkers.Strip(message.Content) : message.Content)));
        turns.Add(asked);

        return new AssembledPrompt(
            turns,
            new TokenCount(used),
            HistoryMessagesIncluded: kept.Count,
            HistoryMessagesOmitted: _history.Count - kept.Count,
            template.Version)
        {
            Sources = included,
            SourcesOmitted = sourcesOmitted,
        };
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
    int TemplateVersion)
{
    /// <summary>The numbered sources the model was given: the only ones it can cite.</summary>
    public IReadOnlyList<PromptSource> Sources { get; init; } = [];

    /// <summary>Retrieved chunks left out because the context budget was spent.</summary>
    public int SourcesOmitted { get; init; }
}
