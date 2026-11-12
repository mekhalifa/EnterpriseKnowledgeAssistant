using EKA.Domain.Prompting;

namespace EKA.Application.Abstractions;

/// <summary>
/// Collection-like access to the <see cref="PromptTemplate"/> aggregate.
/// </summary>
/// <remarks>
/// Prompt templates are operator configuration, not tenant data, so these methods are not
/// tenant-scoped. If prompts ever become customisable per tenant, every signature here
/// gains a <c>TenantId</c> — and that change should be an ADR, because it turns a shared
/// resource into a per-tenant one.
/// </remarks>
public interface IPromptTemplateRepository
{
    /// <summary>
    /// The live version of a named template, or null if none is active. At most one can be
    /// active per name — enforced by a filtered unique index, since the rule spans
    /// instances and no single aggregate can see it.
    /// </summary>
    Task<PromptTemplate?> GetActiveAsync(
        string name,
        CancellationToken cancellationToken = default);

    Task<PromptTemplate?> GetVersionAsync(
        string name,
        int version,
        CancellationToken cancellationToken = default);

    /// <summary>All versions of a template, newest first — for review and for comparing
    /// evaluation runs across prompt revisions.</summary>
    Task<IReadOnlyList<PromptTemplate>> GetVersionsAsync(
        string name,
        CancellationToken cancellationToken = default);

    void Add(PromptTemplate template);
}
