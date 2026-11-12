using EKA.Domain.Providers;

namespace EKA.Application.Abstractions;

/// <summary>
/// Collection-like access to the <see cref="AIProvider"/> aggregate.
/// </summary>
/// <remarks>
/// Operator configuration, like prompt templates: not tenant-scoped. Remember that an
/// <see cref="AIProvider"/> never carries a credential — only
/// <see cref="AIProvider.SecretReference"/>, the name of one. Resolving that reference to
/// an actual value is the job of configuration in <c>EKA.AI</c> at Step 5, not of this
/// repository.
/// </remarks>
public interface IAIProviderRepository
{
    Task<AIProvider?> GetByIdAsync(Guid providerId, CancellationToken cancellationToken = default);

    Task<AIProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AIProvider>> GetEnabledAsync(CancellationToken cancellationToken = default);

    void Add(AIProvider provider);
}
