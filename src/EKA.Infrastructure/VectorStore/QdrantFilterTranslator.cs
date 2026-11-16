using EKA.Application.Retrieval;
using Qdrant.Client.Grpc;
using static Qdrant.Client.Grpc.Conditions;

namespace EKA.Infrastructure.VectorStore;

/// <summary>
/// Turns a <see cref="ChunkFilter"/> into a Qdrant <see cref="Filter"/>: the second
/// interpretation of the Specification, written once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exhaustive by construction.</b> The switch has no default branch that quietly passes.
/// A new kind of condition added to the application without a translation throws here, in the
/// first test that uses it. A translator that skipped what it did not recognize would turn an
/// unknown restriction into no restriction.
/// </para>
/// <para>
/// <b>Refuses a filter with no tenant.</b> The application always builds one with
/// <see cref="ChunkFilter.For"/>, so this never fires in correct code. It exists for the code
/// that is not correct yet.
/// </para>
/// </remarks>
internal static class QdrantFilterTranslator
{
    public static Filter Translate(ChunkFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.PinnedTenant is null)
        {
            throw new ArgumentException(
                "A vector search must be restricted to one tenant. Build the filter with ChunkFilter.For or ChunkFilter.InTenant.",
                nameof(filter));
        }

        return AllOf(filter.Conditions);
    }

    private static Filter AllOf(IEnumerable<ChunkFilter> conditions)
    {
        var translated = new Filter();
        foreach (var condition in conditions)
        {
            translated.Must.Add(Condition(condition));
        }

        return translated;
    }

    private static Condition Condition(ChunkFilter condition) => condition switch
    {
        TenantIs tenant => MatchKeyword(QdrantVectorStore.TenantField, tenant.TenantId.Value.ToString()),

        // Qdrant's "match any": the point's access array shares at least one value with these.
        GrantedToAny granted => Match(QdrantVectorStore.AccessField, granted.PrincipalKeys),

        EmbeddingModelIs model => MatchKeyword(QdrantVectorStore.ModelField, model.Model),

        // Conditions() flattens AllOf, so one here means a nested conjunction from a future
        // change. Translated rather than refused: a conjunction can only narrow.
        AllOf all => new Condition { Filter = AllOf(all.Filters) },

        _ => throw new NotSupportedException(
            $"No Qdrant translation for the filter condition {condition.GetType().Name}. Add one here; never skip it."),
    };
}
