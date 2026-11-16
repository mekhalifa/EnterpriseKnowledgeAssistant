using EKA.Application.Abstractions.Ingestion;
using EKA.Domain.Access;
using EKA.Domain.Common;

namespace EKA.Application.Retrieval;

/// <summary>
/// Which indexed chunks a search may consider: the <b>Specification</b> pattern.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a pattern here, and nowhere else.</b> Retrieval is the one query in this system where
/// a missing condition is a data breach rather than a bug. The conditions are tenant, access
/// list and embedding model, and Step 12 or a later evaluation may add more. Written as
/// <c>if</c>s inside the Qdrant adapter, they would be a boolean expression nobody can test
/// without Qdrant, rebuilt by every caller. As objects, each condition is one small type with
/// two interpretations, both unit-tested:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="IsSatisfiedBy"/>, evaluated in memory, which the fakes and the
/// tests use;</description></item>
/// <item><description>a translation into the vector store's own filter language, written once,
/// in the adapter (<c>QdrantFilterTranslator</c>).</description></item>
/// </list>
/// <para>
/// <b>Only <see cref="And"/>, never <c>Or</c>.</b> Every condition here narrows a search. An
/// <c>Or</c> at the top would let one branch widen it, and "tenant A <i>or</i> group hr" is a
/// query that reads group hr in every tenant. The one disjunction access really needs, "any of
/// the caller's principals", is a single leaf (<see cref="GrantedToAny"/>) whose shape cannot
/// reach outside the conditions around it.
/// </para>
/// <para>
/// <b>The tenant is not optional.</b> <see cref="PinnedTenant"/> finds the tenant condition at
/// the top level, and the vector store refuses a filter without one. A search that forgot it
/// fails loudly instead of returning every tenant's chunks.
/// </para>
/// </remarks>
public abstract record ChunkFilter
{
    private protected ChunkFilter()
    {
    }

    /// <summary>The standard retrieval filter for one caller: their tenant, their access, and
    /// vectors from the model that embedded the question.</summary>
    public static ChunkFilter For(AccessScope caller, string embeddingModel)
    {
        ArgumentNullException.ThrowIfNull(caller);

        return InTenant(caller.TenantId)
            .And(GrantedToAnyOf(caller.Keys))
            .And(EmbeddedWith(embeddingModel));
    }

    public static ChunkFilter InTenant(TenantId tenantId) => new TenantIs(tenantId);

    public static ChunkFilter GrantedToAnyOf(IReadOnlyList<string> principalKeys) => new GrantedToAny(principalKeys);

    public static ChunkFilter EmbeddedWith(string model) => new EmbeddingModelIs(model);

    /// <summary>Both conditions. Nested conjunctions are flattened, so the tree stays one level deep.</summary>
    public ChunkFilter And(ChunkFilter other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new AllOf([.. Flatten(this), .. Flatten(other)]);
    }

    /// <summary>The conditions joined at the top level; just this one for a leaf.</summary>
    public IReadOnlyList<ChunkFilter> Conditions => [.. Flatten(this)];

    /// <summary>
    /// The tenant this filter requires, or null if it requires none. Only a tenant condition at
    /// the top level counts: with no <c>Or</c> in the algebra, that is the only place it can be.
    /// </summary>
    public TenantId? PinnedTenant =>
        Flatten(this).OfType<TenantIs>().Select(condition => (TenantId?)condition.TenantId).FirstOrDefault();

    /// <summary>The in-memory interpretation, against what the store holds for one chunk.</summary>
    public abstract bool IsSatisfiedBy(ChunkVector candidate);

    private static IEnumerable<ChunkFilter> Flatten(ChunkFilter filter) =>
        filter is AllOf all ? all.Filters : [filter];
}

/// <summary>The chunk belongs to this tenant.</summary>
public sealed record TenantIs : ChunkFilter
{
    public TenantIs(TenantId tenantId)
    {
        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("A tenant filter needs a tenant.", nameof(tenantId));
        }

        TenantId = tenantId;
    }

    public TenantId TenantId { get; }

    public override bool IsSatisfiedBy(ChunkVector candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.TenantId == TenantId;
    }
}

/// <summary>
/// The chunk's document grants at least one of these principals (canonical keys such as
/// <c>tenant</c>, <c>user:alice</c>, <c>group:hr</c>).
/// </summary>
public sealed record GrantedToAny : ChunkFilter
{
    public GrantedToAny(IReadOnlyList<string> principalKeys)
    {
        ArgumentNullException.ThrowIfNull(principalKeys);

        // Empty would mean "granted to nobody", which matches nothing. Callers never want that,
        // and a store that read an empty "any of" as "no condition" would match everything.
        // Refusing it removes the question.
        if (principalKeys.Count == 0)
        {
            throw new ArgumentException("An access filter needs at least one principal.", nameof(principalKeys));
        }

        PrincipalKeys = [.. principalKeys.Distinct(StringComparer.Ordinal)];
    }

    public IReadOnlyList<string> PrincipalKeys { get; }

    public override bool IsSatisfiedBy(ChunkVector candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.AccessKeys.Any(key => PrincipalKeys.Contains(key, StringComparer.Ordinal));
    }
}

/// <summary>
/// The chunk was embedded by this model.
/// </summary>
/// <remarks>
/// The collection already refuses vectors of another <i>size</i>. Two models can share a size
/// and still place meanings differently, and a query vector compared with vectors from another
/// model gets scores that look normal and mean nothing. This keeps them apart during a
/// re-embedding.
/// </remarks>
public sealed record EmbeddingModelIs : ChunkFilter
{
    public EmbeddingModelIs(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        Model = model;
    }

    public string Model { get; }

    public override bool IsSatisfiedBy(ChunkVector candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return string.Equals(candidate.EmbeddingModel, Model, StringComparison.Ordinal);
    }
}

/// <summary>Every condition holds. Built by <see cref="ChunkFilter.And"/>, never nested.</summary>
public sealed record AllOf : ChunkFilter
{
    internal AllOf(IReadOnlyList<ChunkFilter> filters) => Filters = filters;

    public IReadOnlyList<ChunkFilter> Filters { get; }

    public override bool IsSatisfiedBy(ChunkVector candidate) =>
        Filters.All(filter => filter.IsSatisfiedBy(candidate));
}
