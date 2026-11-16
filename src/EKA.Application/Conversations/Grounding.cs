using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EKA.Application.Retrieval;
using EKA.Domain.Conversations;

namespace EKA.Application.Conversations;

/// <summary>One retrieved chunk as the model sees it: <c>[Number]</c> in the prompt.</summary>
public sealed record PromptSource(int Number, RetrievedChunk Chunk);

/// <summary>
/// Renders retrieved chunks into the prompt's <c>{{context}}</c> section, as clearly
/// delimited, untrusted data (Step 10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Document text is data, never instructions.</b> Three things keep it that way, and only
/// one of them is the model's good behaviour:
/// </para>
/// <list type="number">
/// <item><description>It never enters the <see cref="MessageRole.System"/> turn. The system turn
/// is the template's text and nothing else (<see cref="PromptBuilder"/>).</description></item>
/// <item><description>Each source sits between <c>&lt;source&gt;</c> tags, and the system
/// instruction tells the model what the tags mean.</description></item>
/// <item><description>A document cannot forge the tags. Any <c>&lt;source</c> or
/// <c>&lt;/source</c> inside a title, heading or chunk is defused before rendering. Otherwise a
/// document could close its own source early and write text that reads as if it came from
/// outside the sources: the classic delimiter break-out.</description></item>
/// </list>
/// <para>
/// None of this makes injection impossible. A model can still be persuaded by text it was told
/// to distrust. It makes the boundary explicit and unforgeable, which is what the
/// application controls. The model's side is tested on the real stack (step-10).
/// </para>
/// </remarks>
public static partial class SourceRenderer
{
    /// <summary>One source's block. The context section is the blocks joined by a blank line.</summary>
    public static string Render(PromptSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var passage = source.Chunk.Passage;
        var attributes = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"id=\"{source.Number}\" title=\"{Attribute(passage.DocumentTitle)}\"");

        if (passage.SectionPath is { } section)
        {
            attributes.Append(CultureInfo.InvariantCulture, $" section=\"{Attribute(section)}\"");
        }

        if (passage.Pages is { } pages)
        {
            attributes.Append(CultureInfo.InvariantCulture, $" pages=\"{pages.Start}-{pages.End}\"");
        }

        return $"<source {attributes}>\n{Defuse(passage.Content.Trim())}\n</source>";
    }

    public static string RenderAll(IEnumerable<PromptSource> sources) =>
        string.Join("\n\n", sources.Select(Render));

    /// <summary>Turns any tag-like <c>&lt;source</c> or <c>&lt;/source</c> into inert text.</summary>
    internal static string Defuse(string text) =>
        SourceTag().Replace(text, match => "&lt;" + match.Value[1..]);

    private static string Attribute(string value) =>
        Defuse(value).Replace('"', '\'').ReplaceLineEndings(" ");

    [GeneratedRegex(@"</?source", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceTag();
}

/// <summary>
/// Reads the <c>[n]</c> markers in an answer, keeps the ones that name a source the model was
/// actually given, and renumbers them in order of first appearance (Step 10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why renumber.</b> The model saw sources [1]..[5] and may cite [3] and [1]. Stored as is,
/// the reader meets [3] first and finds no [2] anywhere. Renumbered by first appearance, the
/// answer reads [1], [2], and the citation list beneath it matches.
/// </para>
/// <para>
/// <b>Invented markers are removed, not kept.</b> A <c>[9]</c> when there were five sources
/// points at nothing. Left in the text, it looks like evidence and is not. Only one to three
/// digits count as a marker, so a year such as <c>[2024]</c> is never touched. The cost: a
/// short bracketed number that was never meant as a citation (a footnote copied from a
/// source) is removed if it is out of range. Recorded as a known limitation.
/// </para>
/// <para>
/// Groups such as <c>[1, 3]</c> and runs such as <c>[1][3]</c> are both understood.
/// </para>
/// </remarks>
public static partial class CitationMarkers
{
    /// <summary>The answer with markers renumbered, and the sources it cites, in marker order.</summary>
    public sealed record Extracted(string Text, IReadOnlyList<PromptSource> Cited);

    public static Extracted Extract(string answer, IReadOnlyList<PromptSource> sources)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(sources);

        var byNumber = sources.ToDictionary(source => source.Number);
        var renumbered = new Dictionary<int, int>();
        var cited = new List<PromptSource>();

        var text = Marker().Replace(answer, match =>
        {
            var kept = new List<int>();
            foreach (var raw in match.Groups["numbers"].Value.Split(',', StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    || !byNumber.TryGetValue(number, out var source))
                {
                    continue; // invented: points at nothing the model was given
                }

                if (!renumbered.TryGetValue(number, out var assigned))
                {
                    assigned = renumbered.Count + 1;
                    renumbered[number] = assigned;
                    cited.Add(source with { Number = assigned });
                }

                if (!kept.Contains(assigned))
                {
                    kept.Add(assigned);
                }
            }

            return kept.Count == 0
                ? string.Empty
                : "[" + string.Join(", ", kept) + "]";
        });

        // A removed marker can leave "days ." or a double space behind.
        text = SpaceBeforePunctuation().Replace(text, "$1");
        text = DoubleSpace().Replace(text, " ").Trim();

        return new Extracted(text, cited);
    }

    /// <summary>
    /// Removes citation markers from earlier answers before they are replayed as history. Their
    /// numbers referred to <i>that</i> turn's sources. Left in, the model would read "[2]" in
    /// history as this turn's source 2.
    /// </summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var stripped = Marker().Replace(text, string.Empty);
        stripped = SpaceBeforePunctuation().Replace(stripped, "$1");
        return DoubleSpace().Replace(stripped, " ").Trim();
    }

    [GeneratedRegex(@"\[(?<numbers>\s*\d{1,3}\s*(?:,\s*\d{1,3}\s*)*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    [GeneratedRegex(@"[ \t]+([.,;:!?)])", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex DoubleSpace();
}
