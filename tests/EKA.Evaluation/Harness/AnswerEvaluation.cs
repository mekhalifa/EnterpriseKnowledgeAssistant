using System.Globalization;
using System.Text;
using EKA.Contracts.Conversations;

namespace EKA.Evaluation.Harness;

internal sealed record AnswerResult(
    EvaluationQuestion Question,
    AskQuestionResponse Response,
    bool CitedExpected,
    Judgement? Judgement,
    IReadOnlyList<string> Violations)
{
    public bool Answered => Response.Grounding == "cited";

    public bool Refused => !Answered;
}

internal sealed record AnswerReport(IReadOnlyList<AnswerResult> Results, string ChatModel, string JudgeModel)
{
    private IEnumerable<AnswerResult> ShouldAnswer => Results.Where(r => r.Question.ExpectsAnswer);

    /// <summary>The gated numbers. Names are the baseline file's keys.</summary>
    public IReadOnlyDictionary<string, double> Metrics => new SortedDictionary<string, double>(StringComparer.Ordinal)
    {
        // Of the questions the documents answer, how many got a cited answer.
        ["answerRate"] = Rate(ShouldAnswer, r => r.Answered),
        // Of those answered, how many cited an expected section (citation correctness).
        ["citationCorrectness"] = Rate(ShouldAnswer.Where(r => r.Answered), r => r.CitedExpected),
        // Of the questions that must be refused, how many were.
        ["refusalCorrectness"] = Rate(Results.Where(r => r.Question.MustRefuse), r => r.Refused),
        // Mean judge score over cited answers (1 supported, 0.5 partial, 0 unsupported).
        ["groundedness"] = Mean(Results.Select(r => r.Judgement?.Score).OfType<double>()),
    };

    public IReadOnlyList<string> Violations => [.. Results.SelectMany(r => r.Violations.Select(v => $"{r.Question.Id}: {v}"))];

    public int Unparseable => Results.Count(r => r.Judgement?.Verdict is Verdict.Unparseable);

    // An empty set scores 0, never 1. The first run of this harness reported groundedness 1.000
    // because every judge call had failed and the mean of nothing was taken as perfect
    // (step-11 section 5.1). A metric with nothing to measure is a failure to measure.
    private static double Rate(IEnumerable<AnswerResult> results, Func<AnswerResult, bool> predicate)
    {
        var list = results.ToList();
        return list.Count == 0 ? 0 : RetrievalReport.Round(list.Count(predicate) / (double)list.Count);
    }

    private static double Mean(IEnumerable<double> scores)
    {
        var list = scores.ToList();
        return list.Count == 0 ? 0 : RetrievalReport.Round(list.Average());
    }

    public string ToMarkdown(Baseline.Comparison comparison, string configuration)
    {
        var markdown = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        markdown.AppendLine("# Answer evaluation").AppendLine();
        markdown.AppendLine(configuration).AppendLine();
        markdown.AppendLine(comparison.ToMarkdown());
        markdown.AppendLine(culture, $"Judge replies that could not be parsed (excluded from groundedness): {Unparseable}.").AppendLine();

        markdown.AppendLine("## Per question").AppendLine();
        markdown.AppendLine("| Id | Kind | Caller | Grounding | Cited expected | Judge | Answer |");
        markdown.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in Results)
        {
            var expected = r.Question.ExpectsAnswer ? (r.CitedExpected ? "yes" : "**no**") : "–";
            var grounding = r.Question.MustRefuse && r.Answered ? $"**{r.Response.Grounding}**" : r.Response.Grounding;
            var judge = r.Judgement is null ? "–" : $"{r.Judgement.Verdict}: {Cell(r.Judgement.Reason)}";
            markdown.AppendLine(culture, $"| {r.Question.Id} | {r.Question.Kind} | {r.Question.Caller} | {grounding} | {expected} | {judge} | {Cell(r.Response.Answer.Content)} |");
        }

        return markdown.ToString();
    }

    private static string Cell(string text) => text.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);
}

internal static class AnswerEvaluation
{
    public static async Task<AnswerReport> RunAsync(EvaluationHost host, IReadOnlyList<EvaluationQuestion> questions, GroundednessJudge judge, string judgeModel)
    {
        var results = new List<AnswerResult>();

        foreach (var question in questions)
        {
            var (response, prompt) = await host.AskAsync(question).ConfigureAwait(false);
            var answer = response.Answer;
            var violations = new List<string>();

            // ── Hard rules: one violation fails the run, whatever the averages say ──────

            if (question.MustRefuse && response.Grounding == "cited")
            {
                violations.Add($"must refuse, but answered citing {string.Join(", ", answer.Citations.Select(c => host.KeyOf(c.DocumentId)))}");
            }

            foreach (var forbidden in question.ForbiddenText)
            {
                if (answer.Content.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"the answer contains \"{forbidden}\"");
                }

                // For a restricted question the forbidden text must not even reach the model.
                if (question.Kind is QuestionKind.Restricted
                    && prompt is not null
                    && prompt.Any(turn => turn.Content.Contains(forbidden, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"the prompt contained \"{forbidden}\"");
                }
            }

            var scope = host.Scope(question.Caller);
            foreach (var citation in answer.Citations)
            {
                if (!host.Documents[host.KeyOf(citation.DocumentId)].IsAccessibleTo(scope))
                {
                    violations.Add($"cited {host.KeyOf(citation.DocumentId)}, which {question.Caller} may not read");
                }
            }

            // ── Measured, not gated one by one ─────────────────────────────────────────

            var citedExpected = answer.Citations.Any(c =>
                question.ExpectedSources.Any(expected => host.Matches(c.DocumentId, c.SectionPath, expected)));

            Judgement? judgement = null;
            if (response.Grounding == "cited")
            {
                judgement = await judge.JudgeAsync(
                    question.Question,
                    answer.Content,
                    [.. answer.Citations.Select(c => (c.Number, host.PassageText(c.DocumentId, c.ChunkIndex)))]).ConfigureAwait(false);

                // An answer the judge could not grade is an answer this run did not evaluate.
                // That fails the run: a gate that passes because its instrument broke is no gate.
                if (judgement.Verdict is Verdict.Unparseable)
                {
                    violations.Add($"the judge could not grade the answer: {judgement.Reason}");
                }
            }

            results.Add(new AnswerResult(question, response, citedExpected, judgement, violations));
        }

        return new AnswerReport(results, host.ChatModel, judgeModel);
    }
}
