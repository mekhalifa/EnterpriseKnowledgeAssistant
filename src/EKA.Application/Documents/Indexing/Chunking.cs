using System.Text.RegularExpressions;
using EKA.Application.Abstractions.AI;
using EKA.Application.Abstractions.Ingestion;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using Microsoft.Extensions.Options;

namespace EKA.Application.Documents.Indexing;

/// <summary>
/// Cuts a document's extracted sections into chunks: the <b>Strategy</b> pattern.
/// </summary>
/// <remarks>
/// <para>
/// Chunking is the single most tuned parameter in a RAG system, and Step 11 will want to
/// compare algorithms on the same documents. Behind this interface, a new algorithm is a new
/// class and one registration. The handler that calls it, the persistence, and the vector
/// store do not change. That is the reason for the pattern here, and it is a real one: the
/// alternatives (semantic splitting, parent/child chunks) are known and likely to be tried.
/// </para>
/// <para>
/// <b>Pure:</b> no I/O, no clock, no ids. The same sections and options always produce the
/// same chunks, which is what makes re-chunking reproducible and the strategy unit-testable.
/// </para>
/// </remarks>
public interface IChunkingStrategy
{
    /// <returns>Chunks in document order. Empty only if every section is blank.</returns>
    IReadOnlyList<ChunkDraft> Chunk(IReadOnlyList<ExtractedSection> sections);
}

/// <summary>A chunk before it becomes a <see cref="DocumentChunk"/>: text plus where it came from.</summary>
public sealed record ChunkDraft(string Content, TokenCount Tokens, PageRange? Pages, string? SectionPath);

/// <summary>
/// The default strategy: section boundaries are hard, and long sections are cut into
/// overlapping windows that break at paragraph, then sentence, ends (ADR-0024).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why sections first.</b> A chunk that spans "Annual leave" and "Sick leave" embeds as a
/// blend of both and cites one heading for text from two. Respecting the extractor's
/// sections keeps every chunk's page and heading <i>exact</i>, which Step 10's citations
/// depend on.
/// </para>
/// <para>
/// <b>Small neighbours merge.</b> A PDF yields one section per page, and a Word file one per
/// heading. A half-empty page or a heading with one sentence under it would otherwise
/// become a tiny chunk whose vector carries almost no meaning. Adjacent sections with the
/// <i>same</i> heading trail are merged while the result still fits one chunk; the chunk's
/// page range then spans them (pp. 3-4). A different heading is never merged across.
/// </para>
/// <para>
/// <b>Windows break at natural ends.</b> A long section is split into units: paragraphs,
/// then sentences for an oversized paragraph, then words for a pathological sentence (a
/// table flattened into one line). Units are packed up to the target, and the next window
/// starts with the last units of the previous one, up to the overlap budget. Overlap is
/// measured in whole units, so a window never starts mid-sentence.
/// </para>
/// </remarks>
public sealed partial class SectionAwareChunker(ITokenCounter tokens, IOptions<IndexingOptions> options) : IChunkingStrategy
{
    private const string ParagraphBreak = "\n\n";

    private readonly int _target = options.Value.ChunkTargetTokens;
    private readonly int _overlap = options.Value.ChunkOverlapTokens;

    public IReadOnlyList<ChunkDraft> Chunk(IReadOnlyList<ExtractedSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        var drafts = new List<ChunkDraft>();

        foreach (var group in MergeSmallNeighbours(sections))
        {
            foreach (var window in Windows(group.Text))
            {
                drafts.Add(new ChunkDraft(window, tokens.Count(window), group.Pages, group.SectionPath));
            }
        }

        return drafts;
    }

    // ── Sections ───────────────────────────────────────────────────────────────────

    private IEnumerable<SectionGroup> MergeSmallNeighbours(IReadOnlyList<ExtractedSection> sections)
    {
        SectionGroup? current = null;

        foreach (var section in sections)
        {
            var text = section.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (current is not null
                && string.Equals(current.SectionPath, section.SectionPath, StringComparison.Ordinal)
                && Count(current.Text + ParagraphBreak + text) <= _target)
            {
                current = current with
                {
                    Text = current.Text + ParagraphBreak + text,
                    LastPage = section.PageNumber ?? current.LastPage,
                };
                continue;
            }

            if (current is not null)
            {
                yield return current;
            }

            current = new SectionGroup(text, section.SectionPath, section.PageNumber, section.PageNumber);
        }

        if (current is not null)
        {
            yield return current;
        }
    }

    // ── Windows ────────────────────────────────────────────────────────────────────

    private List<string> Windows(string text)
    {
        if (Count(text) <= _target)
        {
            return [text];
        }

        var units = Units(text);
        var windows = new List<string>();
        var start = 0;

        while (start < units.Count)
        {
            // Pack units up to the target. The first unit always goes in, so a window always
            // advances, even when a single unit is at the limit.
            var end = start;
            var used = 0;
            while (end < units.Count && (end == start || used + units[end].Tokens <= _target))
            {
                used += units[end].Tokens;
                end++;
            }

            windows.Add(Join(units, start, end));

            if (end == units.Count)
            {
                break;
            }

            // Step back over whole units for the overlap, but never back to `start`, or the
            // next window would repeat this one and the loop would not advance.
            var next = end;
            var overlap = 0;
            while (next - 1 > start && overlap + units[next - 1].Tokens <= _overlap)
            {
                overlap += units[next - 1].Tokens;
                next--;
            }

            start = next;
        }

        return windows;
    }

    /// <summary>Paragraphs; sentences for a paragraph over the target; words for a sentence over it.</summary>
    private List<Unit> Units(string text)
    {
        var units = new List<Unit>();
        var paragraphs = text.Split(ParagraphBreak, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var p = 0; p < paragraphs.Length; p++)
        {
            var joiner = p == 0 ? string.Empty : ParagraphBreak;
            var paragraph = paragraphs[p];

            if (Count(paragraph) <= _target)
            {
                units.Add(Unit.Of(joiner, paragraph, this));
                continue;
            }

            var sentences = SentenceEnd().Split(paragraph);
            for (var s = 0; s < sentences.Length; s++)
            {
                var sentenceJoiner = s == 0 ? joiner : " ";

                if (Count(sentences[s]) <= _target)
                {
                    units.Add(Unit.Of(sentenceJoiner, sentences[s], this));
                    continue;
                }

                var pieces = HardSplit(sentences[s]);
                for (var w = 0; w < pieces.Count; w++)
                {
                    units.Add(Unit.Of(w == 0 ? sentenceJoiner : " ", pieces[w], this));
                }
            }
        }

        return units;
    }

    /// <summary>
    /// Words packed up to the target; a single word longer than that (a URL, base64, a
    /// table flattened into one token) is sliced by length. Rare, and never pretty, but a
    /// chunker that can loop forever or emit an oversized chunk on odd input is worse.
    /// </summary>
    private List<string> HardSplit(string sentence)
    {
        var pieces = new List<string>();
        var current = new List<string>();
        var used = 0;

        foreach (var word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var part in SliceIfLonger(word))
            {
                // With its separator, for the same reason as Unit.Of.
                var cost = current.Count == 0 ? Count(part) : Count(" " + part);
                if (current.Count > 0 && used + cost > _target)
                {
                    pieces.Add(string.Join(' ', current));
                    current.Clear();
                    used = 0;
                }

                current.Add(part);
                used += cost;
            }
        }

        if (current.Count > 0)
        {
            pieces.Add(string.Join(' ', current));
        }

        return pieces;
    }

    private IEnumerable<string> SliceIfLonger(string word)
    {
        var cost = Count(word);
        if (cost <= _target)
        {
            yield return word;
            yield break;
        }

        // Proportional to the counter's own ratio, so this stays right if the estimating
        // counter is replaced by a real tokenizer.
        var length = Math.Max(1, (int)((long)word.Length * _target / cost));
        for (var offset = 0; offset < word.Length; offset += length)
        {
            yield return word.Substring(offset, Math.Min(length, word.Length - offset));
        }
    }

    private static string Join(List<Unit> units, int start, int end)
    {
        var builder = new System.Text.StringBuilder(units[start].Text);
        for (var i = start + 1; i < end; i++)
        {
            builder.Append(units[i].Joiner).Append(units[i].Text);
        }

        return builder.ToString();
    }

    private int Count(string text) => tokens.Count(text).Value;

    /// <summary>Whitespace after a sentence-ending mark. Abbreviations ("e.g. this") split
    /// too early; harmless, because the pieces are packed back together up to the target.</summary>
    [GeneratedRegex(@"(?<=[.!?])\s+(?=\S)")]
    private static partial Regex SentenceEnd();

    /// <param name="Tokens">Counted <i>with</i> the joiner. Summing separately counted pieces
    /// would leave out the separators between them, and a window packed "up to 500" would
    /// come out over. Counting each piece with its separator can only over-count (rounding
    /// up per piece), so a window never exceeds the target; it is at worst a little under.</param>
    private sealed record Unit(string Joiner, string Text, int Tokens)
    {
        public static Unit Of(string joiner, string text, SectionAwareChunker chunker) =>
            new(joiner, text, chunker.Count(joiner + text));
    }

    private sealed record SectionGroup(string Text, string? SectionPath, int? FirstPage, int? LastPage)
    {
        public PageRange? Pages => FirstPage is { } first
            ? new PageRange(first, Math.Max(first, LastPage ?? first))
            : null;
    }
}

/// <summary>
/// The text actually sent to the embedding model for a chunk: its document title and
/// heading trail, then the chunk itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the chunk alone.</b> A chunk that says "Employees receive 25 days" does not say
/// <i>of what</i>. The heading "Benefits / Annual leave" does, and so does the title. With them
/// prepended, the vector sits near questions about annual leave, not near every sentence with
/// a number of days in it. The stored <see cref="DocumentChunk.Content"/> stays the plain
/// text, because that is what a citation shows.
/// </para>
/// <para>
/// Changing this format changes every vector. Like changing the model, it means re-embedding
/// everything, so it is one function, in one place.
/// </para>
/// </remarks>
public static class EmbeddingInput
{
    public static string For(Document document, DocumentChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chunk);

        var heading = chunk.SectionPath is null
            ? document.Title.Value
            : $"{document.Title.Value}\n{chunk.SectionPath}";

        return $"{heading}\n\n{chunk.Content}";
    }

    /// <summary>
    /// The text embedded for a search question (Step 9): the question, trimmed, and nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asymmetric with <see cref="For(Document, DocumentChunk)"/> on purpose. The title and
    /// heading give a <i>passage</i> the context it lacks; a question already says what it is
    /// about, and prefixing anything would pull every query vector toward the same made-up
    /// heading.
    /// </para>
    /// <para>
    /// Here, next to the passage format, because the two are one decision. Models trained with
    /// instructions (E5, BGE, Nomic) expect <c>"query: "</c> and <c>"passage: "</c> prefixes, and
    /// switching to one means changing both functions together, then re-embedding everything.
    /// </para>
    /// </remarks>
    public static string ForQuery(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        return question.Trim();
    }
}
