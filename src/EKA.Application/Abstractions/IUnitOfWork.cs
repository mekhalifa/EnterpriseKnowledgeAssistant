namespace EKA.Application.Abstractions;

/// <summary>
/// Commits all changes staged by repositories as one transaction.
/// </summary>
/// <remarks>
/// <para>
/// Ingesting a document writes the document row, thousands of chunk rows, and a status
/// change. Half-applied, that leaves a "completed" document with no chunks — a state the
/// domain forbids but the database would happily hold.
/// </para>
/// <para>
/// Repositories <b>stage</b> changes; only the use case commits, once, at the end. A
/// repository that calls <see cref="SaveChangesAsync"/> itself destroys the caller's
/// ability to compose a transaction across two aggregates.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>Persists staged changes and dispatches domain events after the commit
    /// succeeds.</summary>
    /// <returns>The number of state entries written.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
