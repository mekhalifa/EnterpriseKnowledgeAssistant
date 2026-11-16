using EKA.Application.Common;
using EKA.Application.Usage;
using EKA.Contracts.Retrieval;
using EKA.Domain.Access;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace EKA.Application.Retrieval.SearchChunks;

/// <summary>Finds the chunks the caller may read that best match a question.</summary>
/// <param name="Caller">Who is asking, from the token. Decides what may be returned.</param>
/// <param name="Query">The question.</param>
/// <param name="TopK">Requested result count, or null for the configured default.</param>
public sealed record SearchChunksQuery(AccessScope Caller, string Query, int? TopK);

/// <summary>
/// The search endpoint's use case: <see cref="ChunkRetriever"/>, mapped to the contract.
/// </summary>
/// <remarks>
/// Thin on purpose. Everything that decides <i>what</i> is returned lives in the retriever,
/// which Step 10 calls directly, so the endpoint and answer generation cannot drift apart.
/// </remarks>
public sealed class SearchChunksHandler(ChunkRetriever retriever, TokenBudget budget)
{
    public async Task<Result<SearchResponse>> HandleAsync(SearchChunksQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Step 12: a spent monthly budget stops the embedding call before it is made.
        var available = await budget.EnsureAvailableAsync(query.Caller.TenantId, cancellationToken).ConfigureAwait(false);
        if (available.IsFailure)
        {
            return available.Error;
        }

        var result = await retriever.RetrieveAsync(query.Caller, query.Query, query.TopK, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error;
        }

        return new SearchResponse(
            retriever.EffectiveTopK(query.TopK),
            [
                .. result.Value.Select(chunk => new SearchHitResponse(
                    chunk.Passage.DocumentId,
                    chunk.Passage.DocumentTitle,
                    chunk.Passage.Index.Value,
                    chunk.Passage.Content,
                    Math.Round(chunk.Score.Value, 4),
                    chunk.Passage.Pages?.Start,
                    chunk.Passage.Pages?.End,
                    chunk.Passage.SectionPath)),
            ]);
    }
}

/// <summary>Shape checks for <see cref="SearchRequest"/>. No I/O (ADR-0013).</summary>
/// <remarks>
/// A <c>TopK</c> above the server's maximum is clamped, not refused, like a page size. A
/// <c>TopK</c> of zero or less is a broken client, and is refused.
/// </remarks>
public sealed class SearchRequestValidator : AbstractValidator<SearchRequest>
{
    public SearchRequestValidator(IOptions<RetrievalOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        RuleFor(request => request.Query)
            .NotEmpty()
            .Must(query => !string.IsNullOrWhiteSpace(query)).WithMessage("'Query' must contain more than whitespace.")
            .MaximumLength(options.Value.MaxQueryLength);

        RuleFor(request => request.TopK)
            .GreaterThan(0)
            .When(request => request.TopK is not null);
    }
}
