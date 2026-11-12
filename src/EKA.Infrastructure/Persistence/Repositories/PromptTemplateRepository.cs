using EKA.Application.Abstractions;
using EKA.Domain.Prompting;
using Microsoft.EntityFrameworkCore;

namespace EKA.Infrastructure.Persistence.Repositories;

internal sealed class PromptTemplateRepository : IPromptTemplateRepository
{
    private readonly EkaDbContext _context;

    public PromptTemplateRepository(EkaDbContext context)
    {
        _context = context;
    }

    /// <remarks>
    /// Returns a single template rather than a list because the filtered unique index
    /// <c>UX_PromptTemplates_Name_Active</c> guarantees at most one active version per
    /// name. The signature encodes a database constraint, so a caller cannot write code
    /// that handles "two active templates" — a case that cannot occur.
    /// </remarks>
    public Task<PromptTemplate?> GetActiveAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        _context.PromptTemplates
            .FirstOrDefaultAsync(
                template => template.Name == name && template.IsActive,
                cancellationToken);

    public Task<PromptTemplate?> GetVersionAsync(
        string name,
        int version,
        CancellationToken cancellationToken = default) =>
        _context.PromptTemplates
            .FirstOrDefaultAsync(
                template => template.Name == name && template.Version == version,
                cancellationToken);

    public async Task<IReadOnlyList<PromptTemplate>> GetVersionsAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        await _context.PromptTemplates
            .Where(template => template.Name == name)
            .OrderByDescending(template => template.Version)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void Add(PromptTemplate template) => _context.PromptTemplates.Add(template);
}
