namespace EKA.Contracts.Retrieval;

/// <summary>A semantic search over the documents the caller may read.</summary>
/// <param name="Query">The question, in natural language.</param>
/// <param name="TopK">How many chunks to return. Optional; the server applies its default and
/// caps it at its maximum.</param>
/// <remarks>
/// Sent as a POST body, not a query string. Questions can be long, and they can be as
/// sensitive as the documents: a URL ends up in proxy logs, browser history and the
/// <c>Referer</c> header, and a body does not.
/// </remarks>
public sealed record SearchRequest(string Query, int? TopK = null);

/// <param name="TopK">The number of chunks the server actually looked for, after its cap.</param>
/// <param name="Hits">Best first. Empty when nothing the caller may read is relevant enough.</param>
public sealed record SearchResponse(int TopK, IReadOnlyList<SearchHitResponse> Hits);

/// <summary>One retrieved chunk, with everything a citation needs.</summary>
/// <param name="DocumentId">The document the chunk belongs to.</param>
/// <param name="DocumentTitle">Its title at the time of the search.</param>
/// <param name="ChunkIndex">The chunk's position in the document, from 0.</param>
/// <param name="Content">The chunk's text.</param>
/// <param name="Score">Similarity from 0 to 1, where 1 is identical meaning.</param>
/// <param name="PageStart">First page, for paged formats; otherwise null.</param>
/// <param name="PageEnd">Last page, for paged formats; otherwise null.</param>
/// <param name="SectionPath">Heading trail, such as "Benefits / Leave", if known.</param>
public sealed record SearchHitResponse(
    Guid DocumentId,
    string DocumentTitle,
    int ChunkIndex,
    string Content,
    double Score,
    int? PageStart,
    int? PageEnd,
    string? SectionPath);
