using EKA.Application.Abstractions;
using EKA.Domain.Providers;
using Microsoft.EntityFrameworkCore;

namespace EKA.Infrastructure.Persistence.Repositories;

internal sealed class AIProviderRepository : IAIProviderRepository
{
    private readonly EkaDbContext _context;

    public AIProviderRepository(EkaDbContext context)
    {
        _context = context;
    }

    public Task<AIProvider?> GetByIdAsync(
        Guid providerId,
        CancellationToken cancellationToken = default) =>
        _context.AIProviders
            .FirstOrDefaultAsync(provider => provider.Id == providerId, cancellationToken);

    public Task<AIProvider?> GetByNameAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        _context.AIProviders
            .FirstOrDefaultAsync(provider => provider.Name == name, cancellationToken);

    /// <remarks>
    /// Only enabled providers, because a disabled provider is one an operator has
    /// deliberately taken out of service — returning it would let a request route to a
    /// backend someone switched off on purpose.
    /// </remarks>
    public async Task<IReadOnlyList<AIProvider>> GetEnabledAsync(
        CancellationToken cancellationToken = default) =>
        await _context.AIProviders
            .Where(provider => provider.IsEnabled)
            .OrderBy(provider => provider.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void Add(AIProvider provider) => _context.AIProviders.Add(provider);
}
