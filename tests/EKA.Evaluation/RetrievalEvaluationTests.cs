using System.Globalization;
using EKA.Evaluation.Harness;
using Shouldly;
using Xunit.Abstractions;

namespace EKA.Evaluation;

/// <summary>
/// Tier 1: retrieval quality over the fixed corpus, on every <c>dotnet test</c>. Free and
/// deterministic: recorded embeddings, exact search, the shipped retriever and configuration.
/// </summary>
/// <remarks>
/// Fails when retrieval gets worse than the checked-in baseline by any amount, or when retrieval
/// ever returns text the caller may not read. Re-record and re-baseline with
/// <c>EKA_EVAL_RECORD=1 EKA_EVAL_UPDATE_BASELINE=1</c> after a deliberate change.
/// </remarks>
public sealed class RetrievalEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Retrieval_quality_does_not_fall_below_the_baseline_and_never_crosses_access()
    {
        await using var host = await EvaluationHost.StartAsync(withChat: false);
        var report = await RetrievalEvaluation.RunAsync(host, EvaluationSet.LoadQuestions());

        if (EvaluationSettings.Record)
        {
            host.Embeddings.Save();
        }

        var configuration = Configuration(host);

        // Saved before comparing, so a recording run compares against what it just recorded. A
        // run that crossed access never becomes the baseline.
        if (EvaluationSettings.UpdateBaseline && report.Violations.Count == 0)
        {
            Baseline.Save("retrieval", report.Metrics, configuration);
        }

        var comparison = Baseline.Compare("retrieval", report.Metrics, tolerance: 0);
        var path = Baseline.WriteReport("retrieval.md", report.ToMarkdown(comparison, Describe(configuration)));
        output.WriteLine($"Report: {path}");
        output.WriteLine(comparison.ToMarkdown());

        report.Violations.ShouldBeEmpty("Retrieval returned content outside the caller's access scope.");
        comparison.Recorded.ShouldNotBeNull("No retrieval baseline is checked in. Record one: EKA_EVAL_UPDATE_BASELINE=1.");
        comparison.Regressions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_expected_section_in_the_question_set_exists_in_the_ingested_corpus()
    {
        // A typo in questions.json ("Sick-leave") would make a question unanswerable by
        // construction, and it would count as a retrieval miss forever.
        await using var host = await EvaluationHost.StartAsync(withChat: false);

        var sections = host.Documents.Values
            .SelectMany(document => document.Chunks.Select(chunk => (document.Id, chunk.SectionPath)))
            .ToList();

        foreach (var question in EvaluationSet.LoadQuestions())
        {
            foreach (var expected in question.ExpectedSources)
            {
                sections.Any(section => host.Matches(section.Id, section.SectionPath, expected))
                    .ShouldBeTrue($"{question.Id}: no chunk of '{expected.Document}' has the heading '{expected.Section}'.");
            }
        }
    }

    internal static Dictionary<string, string> Configuration(EvaluationHost host) => new(StringComparer.Ordinal)
    {
        ["embeddingModel"] = host.Embeddings.Model,
        ["topK"] = host.RetrievalOptions.DefaultTopK.ToString(CultureInfo.InvariantCulture),
        ["minimumScore"] = host.RetrievalOptions.MinimumScore.ToString(CultureInfo.InvariantCulture),
        ["chunkTargetTokens"] = host.IndexingOptions.ChunkTargetTokens.ToString(CultureInfo.InvariantCulture),
        ["chunkOverlapTokens"] = host.IndexingOptions.ChunkOverlapTokens.ToString(CultureInfo.InvariantCulture),
        ["questions"] = EvaluationSet.LoadQuestions().Count.ToString(CultureInfo.InvariantCulture),
    };

    internal static string Describe(IReadOnlyDictionary<string, string> configuration) =>
        "Configuration: " + string.Join(", ", configuration.Select(pair => $"`{pair.Key}={pair.Value}`")) + ".";
}
