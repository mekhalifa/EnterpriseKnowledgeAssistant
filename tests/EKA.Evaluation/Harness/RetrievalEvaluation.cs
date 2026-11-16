using System.Globalization;
using System.Text;

namespace EKA.Evaluation.Harness;

/// <summary>One question's retrieval: where the first relevant chunk ranked, and what came first.</summary>
/// <param name="Rank">1-based rank of the first chunk matching an expected source, or null if none
/// was returned.</param>
/// <param name="Scores">Every candidate above a zero floor, best first, flagged relevant or not: the
/// raw material for the floor sweep.</param>
internal sealed record RetrievalResult(
    EvaluationQuestion Question,
    int Returned,
    int? Rank,
    string? TopSource,
    double? TopScore,
    IReadOnlyList<(double Score, bool Relevant)> Scores,
    IReadOnlyList<string> Violations);

internal sealed record RetrievalReport(IReadOnlyList<RetrievalResult> Results, int TopK, double Floor)
{
    private IEnumerable<RetrievalResult> Scored => Results.Where(r => r.Question.ExpectsAnswer);

    private IEnumerable<RetrievalResult> OffTopic => Results.Where(r => r.Question.Kind is QuestionKind.OffTopic);

    /// <summary>The gated numbers. Names are the baseline file's keys.</summary>
    public IReadOnlyDictionary<string, double> Metrics => new SortedDictionary<string, double>(StringComparer.Ordinal)
    {
        ["hitAt1"] = HitAt(1),
        ["hitAt3"] = HitAt(3),
        ["hitAtK"] = HitAt(TopK),
        ["mrr"] = Round(Scored.Average(r => r.Rank is { } rank ? 1d / rank : 0d)),
        ["offTopicNoEvidence"] = Round(OffTopic.Average(r => r.Returned == 0 ? 1d : 0d)),
    };

    public IReadOnlyList<string> Violations => [.. Results.SelectMany(r => r.Violations.Select(v => $"{r.Question.Id}: {v}"))];

    private double HitAt(int k) => Round(Scored.Average(r => r.Rank is { } rank && rank <= k ? 1d : 0d));

    internal static double Round(double value) => Math.Round(value, 4);

    /// <summary>
    /// What hit@K and the off-topic refusal rate would be at other floors. A floor is a
    /// trade-off, and this table is the evidence for choosing it.
    /// </summary>
    public IReadOnlyList<(double Floor, double HitAtK, double OffTopicEmpty, double UnanswerableEmpty)> FloorSweep()
    {
        var floors = Enumerable.Range(0, 11).Select(i => 0.55 + (i * 0.025));

        return
        [
            .. floors.Select(floor => (
                Floor: Math.Round(floor, 3),
                HitAtK: Round(Scored.Average(r => r.Scores.Where(s => s.Score >= floor).Take(TopK).Any(s => s.Relevant) ? 1d : 0d)),
                OffTopicEmpty: Round(OffTopic.Average(r => r.Scores.Any(s => s.Score >= floor) ? 0d : 1d)),
                UnanswerableEmpty: Round(Results.Where(r => r.Question.Kind is QuestionKind.Unanswerable)
                    .Average(r => r.Scores.Any(s => s.Score >= floor) ? 0d : 1d)))),
        ];
    }

    public string ToMarkdown(Baseline.Comparison comparison, string configuration)
    {
        var markdown = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        markdown.AppendLine("# Retrieval evaluation").AppendLine();
        markdown.AppendLine(configuration).AppendLine();
        markdown.AppendLine(comparison.ToMarkdown()).AppendLine();

        var relevantTop = Scored.Where(r => r.Rank == 1).Select(r => r.TopScore ?? 0).DefaultIfEmpty().Min();
        var offTopicMax = OffTopic.SelectMany(r => r.Scores.Take(1)).Select(s => s.Score).DefaultIfEmpty().Max();
        markdown.AppendLine("## Score floor");
        markdown.AppendLine();
        markdown.AppendLine(culture, $"Configured floor: **{Floor:0.###}**. Lowest score of a correct top hit: **{relevantTop:0.000}**. Highest score any off-topic question reached: **{offTopicMax:0.000}**.");
        markdown.AppendLine();
        markdown.AppendLine("| Floor | hit@K | Off-topic refused | Unanswerable with no evidence |");
        markdown.AppendLine("|---|---|---|---|");
        foreach (var row in FloorSweep())
        {
            markdown.AppendLine(culture, $"| {row.Floor:0.000}{(Math.Abs(row.Floor - Floor) < 0.001 ? " (configured)" : string.Empty)} | {row.HitAtK:0.00} | {row.OffTopicEmpty:0.00} | {row.UnanswerableEmpty:0.00} |");
        }

        markdown.AppendLine();
        markdown.AppendLine("## Per question");
        markdown.AppendLine();
        markdown.AppendLine("| Id | Kind | Caller | Question | Returned | Rank of expected | Top source (score) |");
        markdown.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in Results)
        {
            var rank = r.Question.ExpectsAnswer ? r.Rank?.ToString(culture) ?? "**miss**" : "–";
            markdown.AppendLine(culture, $"| {r.Question.Id} | {r.Question.Kind} | {r.Question.Caller} | {r.Question.Question} | {r.Returned} | {rank} | {r.TopSource ?? "–"}{(r.TopScore is { } s ? $" ({s:0.000})" : string.Empty)} |");
        }

        return markdown.ToString();
    }
}

internal static class RetrievalEvaluation
{
    public static async Task<RetrievalReport> RunAsync(EvaluationHost host, IReadOnlyList<EvaluationQuestion> questions)
    {
        var results = new List<RetrievalResult>();

        foreach (var question in questions)
        {
            // History questions are retrieved for too, unscored, exactly as they are when the
            // answer tier asks them. Both tiers then embed the same inputs, and recording either
            // one keeps the cache complete for the other.
            foreach (var earlier in question.History ?? [])
            {
                _ = await host.RetrieveAsync(question.Caller, earlier).ConfigureAwait(false);
            }

            // As production embeds it: the latest question only, never the history (ADR-0029).
            var shipped = (await host.RetrieveAsync(question.Caller, question.Question).ConfigureAwait(false)).Value;
            var unfiltered = (await host.RetrieveUnfilteredAsync(question.Caller, question.Question).ConfigureAwait(false)).Value;

            bool Relevant(Application.Retrieval.RetrievedChunk chunk) =>
                question.ExpectedSources.Any(expected => host.Matches(chunk.Passage.DocumentId, chunk.Passage.SectionPath, expected));

            int? rank = null;
            for (var i = 0; i < shipped.Count && rank is null; i++)
            {
                if (Relevant(shipped[i]))
                {
                    rank = i + 1;
                }
            }

            // Hard rule: retrieval never returns text the caller may not read. Checked on the
            // text itself, through the canaries, not only through the access filter that is
            // supposed to guarantee it.
            var violations = new List<string>();
            var scope = host.Scope(question.Caller);
            foreach (var chunk in unfiltered)
            {
                var document = host.Documents.Values.Single(d => d.Id == chunk.Passage.DocumentId);
                if (!document.IsAccessibleTo(scope))
                {
                    violations.Add($"retrieved {host.KeyOf(document.Id)}, which {question.Caller} may not read");
                }

                foreach (var forbidden in question.ForbiddenText.Where(f => chunk.Passage.Content.Contains(f, StringComparison.OrdinalIgnoreCase)))
                {
                    if (question.Kind is QuestionKind.Restricted)
                    {
                        violations.Add($"retrieved text containing \"{forbidden}\"");
                    }
                }
            }

            var top = shipped.FirstOrDefault();
            results.Add(new RetrievalResult(
                question,
                shipped.Count,
                rank,
                top is null ? null : $"{host.KeyOf(top.Passage.DocumentId)} / {top.Passage.SectionPath}",
                top?.Score.Value,
                [.. unfiltered.Select(chunk => (chunk.Score.Value, Relevant(chunk)))],
                violations));
        }

        return new RetrievalReport(results, host.RetrievalOptions.DefaultTopK, host.RetrievalOptions.MinimumScore);
    }
}
