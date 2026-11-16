using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EKA.Evaluation.Harness;

/// <summary>
/// The checked-in record of what the system scored, and the comparison that turns a drop into
/// a failing build.
/// </summary>
/// <remarks>
/// <para>
/// <b>Relative to a baseline, not absolute thresholds.</b> A fixed bar ("hit@5 at least 0.9")
/// cannot see 0.98 falling to 0.91, and today any bar would be a guess. The baseline is what the
/// system actually did, so any drop is visible.
/// </para>
/// <para>
/// <b>Tolerance per tier.</b> Retrieval is deterministic (recorded embeddings, exact search):
/// tolerance zero, any drop fails. Answers come from a model that is not bit-for-bit
/// deterministic even at temperature 0: tolerance 0.10, a little more than one question's worth
/// in each bucket, so noise does not fail the build and a real regression does.
/// </para>
/// <para>
/// <b>Improvements do not update the baseline by themselves.</b> A higher score passes, and the
/// report says so. Raising the bar is a deliberate commit (<c>EKA_EVAL_UPDATE_BASELINE=1</c>),
/// reviewed in the diff, so the bar never moves without someone deciding it should.
/// </para>
/// </remarks>
internal static class Baseline
{
    internal sealed record File(IReadOnlyDictionary<string, double> Metrics, IReadOnlyDictionary<string, string> Configuration, DateTimeOffset RecordedAt);

    internal sealed record Comparison(
        IReadOnlyDictionary<string, double> Current,
        IReadOnlyDictionary<string, double>? Recorded,
        double Tolerance)
    {
        /// <summary>Metrics that fell below the baseline by more than the tolerance.</summary>
        public IReadOnlyList<string> Regressions =>
            Recorded is null
                ? []
                :
                [
                    .. Recorded
                        .Where(pair => !Current.TryGetValue(pair.Key, out var now) || now < pair.Value - Tolerance - 1e-9)
                        .Select(pair => Current.TryGetValue(pair.Key, out var now)
                            ? string.Create(CultureInfo.InvariantCulture, $"{pair.Key}: {now:0.000} is below the baseline {pair.Value:0.000} (tolerance {Tolerance:0.00})")
                            : $"{pair.Key}: no longer measured"),
                ];

        public string ToMarkdown()
        {
            var markdown = new StringBuilder();
            markdown.AppendLine("| Metric | This run | Baseline | |");
            markdown.AppendLine("|---|---|---|---|");
            foreach (var (name, value) in Current)
            {
                var recorded = Recorded is not null && Recorded.TryGetValue(name, out var r) ? r : (double?)null;
                var verdict = recorded switch
                {
                    null => "new",
                    _ when value < recorded - Tolerance - 1e-9 => "**regression**",
                    _ when value > recorded + 1e-9 => "better",
                    _ => "ok",
                };

                markdown.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {value:0.000} | {(recorded is { } b ? b.ToString("0.000", CultureInfo.InvariantCulture) : "–")} | {verdict} |");
            }

            return markdown.ToString();
        }
    }

    public static Comparison Compare(string name, IReadOnlyDictionary<string, double> current, double tolerance) =>
        new(current, Load(name)?.Metrics, tolerance);

    public static File? Load(string name)
    {
        var path = PathOf(name);
        return System.IO.File.Exists(path)
            ? JsonSerializer.Deserialize<File>(System.IO.File.ReadAllText(path), EvaluationSet.Json)
            : null;
    }

    public static void Save(string name, IReadOnlyDictionary<string, double> metrics, IReadOnlyDictionary<string, string> configuration)
    {
        Directory.CreateDirectory(EvaluationSettings.BaselineDirectory);
        System.IO.File.WriteAllText(
            PathOf(name),
            JsonSerializer.Serialize(new File(metrics, configuration, DateTimeOffset.UtcNow), EvaluationSet.Json));
    }

    /// <summary>Writes a report to this run's output, and to docs/evaluation/ when recording a new baseline.</summary>
    public static string WriteReport(string fileName, string markdown)
    {
        Directory.CreateDirectory(EvaluationSettings.RunOutputDirectory);
        var path = Path.Combine(EvaluationSettings.RunOutputDirectory, fileName);
        System.IO.File.WriteAllText(path, markdown);

        if (EvaluationSettings.UpdateBaseline)
        {
            Directory.CreateDirectory(EvaluationSettings.DocsDirectory);
            System.IO.File.WriteAllText(Path.Combine(EvaluationSettings.DocsDirectory, fileName), markdown);
        }

        return path;
    }

    private static string PathOf(string name) => Path.Combine(EvaluationSettings.BaselineDirectory, $"{name}.json");
}
