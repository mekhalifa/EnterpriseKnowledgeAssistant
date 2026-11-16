using EKA.Application.Abstractions.Retrieval;
using EKA.Domain.Access;
using EKA.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace EKA.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IChunkReader"/>: the SQL half of retrieval.
/// </summary>
/// <remarks>
/// <para>
/// <b>A read model, not the aggregate.</b> Loading each <c>Document</c> with its chunks to
/// quote five passages would pull thousands of rows. This projects exactly what a citation
/// needs, with no tracking, in one query.
/// </para>
/// <para>
/// <b>Every check is in the WHERE clause</b>: tenant, access, Completed, and the document ids.
/// The chunk-index half of each key is matched in memory afterwards, among at most a few dozen
/// rows: SQL Server has no tuple <c>IN</c>, and the alternatives (a table-valued parameter, or
/// an <c>OR</c> per pair) cost more code than they save at this size.
/// </para>
/// </remarks>
internal sealed class ChunkReader(EkaDbContext context) : IChunkReader
{
    public async Task<IReadOnlyList<ChunkPassage>> ReadSearchableAsync(
        AccessScope caller,
        IReadOnlyCollection<ChunkKey> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            return [];
        }

        var tenantId = caller.TenantId;
        var callerKeys = caller.Keys.ToList();
        var documentIds = keys.Select(key => key.DocumentId).Distinct().ToList();
        var indexes = keys.Select(key => key.Index).Distinct().ToList();

        var rows = await (
                from document in context.Documents.AsNoTracking()
                join chunk in context.DocumentChunks.AsNoTracking() on document.Id equals chunk.DocumentId
                where document.TenantId == tenantId
                      && document.Status == IngestionStatus.Completed
                      && documentIds.Contains(document.Id)
                      && indexes.Contains(chunk.Index)
                      && document.AccessKeys.Any(key => callerKeys.Contains(key))
                select new
                {
                    document.Id,
                    Title = document.Title,
                    chunk.Index,
                    chunk.Content,
                    chunk.PageRange,
                    chunk.SectionPath,
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var wanted = keys.ToHashSet();

        return
        [
            .. rows
                .Where(row => wanted.Contains(new ChunkKey(row.Id, row.Index)))
                .Select(row => new ChunkPassage(row.Id, row.Title.Value, row.Index, row.Content, row.PageRange, row.SectionPath)),
        ];
    }
}
