using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using EKA.Domain.Retrieval;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EKA.Infrastructure.Persistence;

/// <summary>
/// Converters mapping single-value domain value objects onto primitive columns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why converters rather than owned types.</b> A value object wrapping one primitive
/// should be one column. An owned type would create either a separate table or an
/// awkwardly-named nested column, and — critically — a converted property can be
/// <b>indexed and compared in SQL</b>. <c>WHERE TenantId = @p0</c> is a seek; an owned type
/// would not translate as cleanly. Composite value objects (<c>FileMetadata</c>,
/// <c>PageRange</c>, <c>Citation</c>) genuinely have several columns and use owned types
/// instead.
/// </para>
/// <para>
/// <b>Why they live in one file and are registered once.</b> See
/// <see cref="EkaDbContext.ConfigureConventions"/> — registering by CLR type means a new
/// entity using <c>TenantId</c> is mapped correctly without anyone remembering to repeat
/// the conversion. Repeating it per configuration is how one property ends up unconverted.
/// </para>
/// <para>
/// Note that materialization runs each value object's constructor, so its guards execute
/// against data coming <i>out</i> of the database too. That is deliberate: invalid rows
/// fail loudly at read time rather than propagating silently.
/// </para>
/// </remarks>
internal static class ValueObjectConverters
{
    internal sealed class TenantIdConverter : ValueConverter<TenantId, Guid>
    {
        public TenantIdConverter()
            : base(id => id.Value, value => new TenantId(value))
        {
        }
    }

    internal sealed class DocumentTitleConverter : ValueConverter<DocumentTitle, string>
    {
        public DocumentTitleConverter()
            : base(title => title.Value, value => new DocumentTitle(value))
        {
        }
    }

    internal sealed class ChunkIndexConverter : ValueConverter<ChunkIndex, int>
    {
        public ChunkIndexConverter()
            : base(index => index.Value, value => new ChunkIndex(value))
        {
        }
    }

    internal sealed class TokenCountConverter : ValueConverter<TokenCount, int>
    {
        public TokenCountConverter()
            : base(count => count.Value, value => new TokenCount(value))
        {
        }
    }

    internal sealed class AuthorityScoreConverter : ValueConverter<AuthorityScore, int>
    {
        public AuthorityScoreConverter()
            : base(score => score.Value, value => new AuthorityScore(value))
        {
        }
    }

    internal sealed class SimilarityScoreConverter : ValueConverter<SimilarityScore, double>
    {
        public SimilarityScoreConverter()
            : base(score => score.Value, value => new SimilarityScore(value))
        {
        }
    }
}
