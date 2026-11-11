using EKA.Domain.Common;

namespace EKA.Domain.Prompting;

/// <summary>
/// A versioned, named prompt: the system instruction plus the user-message shape used to
/// ask the model a grounded question.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why versioned rather than edited in place.</b> Prompt wording is the single biggest
/// lever on answer quality, and changing it changes behaviour as much as changing code.
/// Editing a template in place makes the Step 11 evaluation meaningless — a score recorded
/// last week describes a prompt that no longer exists, so a regression is
/// indistinguishable from an improvement. New wording is a new version; old versions stay
/// readable.
/// </para>
/// <para>
/// <b>The provider association is required.</b> This is the README's P1 item. A prompt
/// tuned for one model is not portable to another: instruction-following, refusal
/// behaviour, and context handling all differ. A template with no declared provider is a
/// template nobody can safely run, so the constructor refuses to build one — making the
/// relationship explicit in the type rather than a nullable column that is "usually" set.
/// </para>
/// <para>
/// <b>One invariant lives outside this class:</b> at most one <i>active</i> version per
/// name. That spans multiple instances, so no single aggregate can enforce it; Step 3 adds
/// a filtered unique index on <c>(Name) WHERE IsActive = 1</c>. Same pattern as chunk
/// ordering — when an invariant cannot live in the aggregate, it moves to a constraint
/// that cannot be bypassed.
/// </para>
/// </remarks>
public sealed class PromptTemplate : EntityAudit<Guid>
{
    /// <summary>Placeholder the user's question is substituted into. Mandatory.</summary>
    public const string QuestionPlaceholder = "{{question}}";

    /// <summary>Placeholder retrieved chunks are substituted into.</summary>
    public const string ContextPlaceholder = "{{context}}";

    /// <summary>EF Core materialization only.</summary>
    private PromptTemplate()
    {
    }

    public PromptTemplate(
        Guid id,
        string name,
        int version,
        string systemPrompt,
        string userPromptTemplate,
        Guid aiProviderId,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Prompt template name is required.");
        }

        if (version < 1)
        {
            throw new DomainException($"Prompt template version starts at 1; got {version}.");
        }

        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            throw new DomainException("A prompt template must have a system instruction.");
        }

        if (string.IsNullOrWhiteSpace(userPromptTemplate))
        {
            throw new DomainException("A prompt template must have a user prompt.");
        }

        // Without this guard, a template missing the placeholder produces a prompt that
        // never contains the user's question. The model answers something plausible from
        // the context alone, no error is raised anywhere, and the failure looks like poor
        // answer quality rather than a broken template.
        if (!userPromptTemplate.Contains(QuestionPlaceholder, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"User prompt must contain the {QuestionPlaceholder} placeholder, "
                + "otherwise the question is never sent to the model.");
        }

        if (aiProviderId == Guid.Empty)
        {
            throw new DomainException(
                "A prompt template must declare which AI provider it was written for.");
        }

        Name = name.Trim();
        Version = version;
        SystemPrompt = systemPrompt.Trim();
        UserPromptTemplate = userPromptTemplate;
        AIProviderId = aiProviderId;
        IsActive = false;
    }

    public string Name { get; private set; } = string.Empty;

    public int Version { get; private set; }

    /// <summary>
    /// Operator instructions — tone, refusal policy, citation requirements. Never contains
    /// user or document content; see <c>MessageRole</c> for why that boundary matters.
    /// </summary>
    public string SystemPrompt { get; private set; } = string.Empty;

    /// <summary>
    /// The user-message shape, containing <see cref="QuestionPlaceholder"/> and usually
    /// <see cref="ContextPlaceholder"/>. Not trimmed — whitespace and newlines around
    /// placeholders are meaningful to the model.
    /// </summary>
    public string UserPromptTemplate { get; private set; } = string.Empty;

    public Guid AIProviderId { get; private set; }

    /// <summary>
    /// Whether this version is the one in use. New templates start inactive so they can be
    /// reviewed and evaluated before they affect anyone's answers.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// True when the template feeds retrieved chunks to the model. A template without
    /// <see cref="ContextPlaceholder"/> is a plain chat prompt — legitimate, but it cannot
    /// produce grounded answers, so Step 10 refuses to use one for RAG.
    /// </summary>
    public bool SupportsGrounding =>
        UserPromptTemplate.Contains(ContextPlaceholder, StringComparison.Ordinal);

    public void Activate(DateTimeOffset at)
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        MarkModified(at);
    }

    public void Deactivate(DateTimeOffset at)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        MarkModified(at);
    }

    /// <summary>
    /// Creates the next version of this template with new wording, inactive and ready for
    /// evaluation. The original is left untouched.
    /// </summary>
    public PromptTemplate CreateNextVersion(
        Guid id,
        string systemPrompt,
        string userPromptTemplate,
        DateTimeOffset createdAt) =>
        new(id, Name, Version + 1, systemPrompt, userPromptTemplate, AIProviderId, createdAt);

    public override string ToString() =>
        $"{Name} v{Version}{(IsActive ? " (active)" : string.Empty)}";
}
