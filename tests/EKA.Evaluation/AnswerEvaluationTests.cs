using EKA.AI;
using EKA.Application.Abstractions.AI;
using EKA.Evaluation.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit.Abstractions;

namespace EKA.Evaluation;

/// <summary>
/// Tier 2: answers from the real model, judged. Billed (about ten cents), so opt-in twice: a
/// key must be configured, and <c>EKA_EVAL_ANSWERS=1</c> must be set.
/// </summary>
/// <remarks>
/// <code>
/// $env:EKA_EVAL_ANSWERS = "1"; dotnet test tests/EKA.Evaluation --filter Category=Evaluation
/// </code>
/// Fails on any hard-rule violation (a must-refuse question answered, a canary in an answer or a
/// restricted prompt, a restricted document cited), or when a metric falls more than 0.10 below
/// the checked-in baseline.
/// </remarks>
[Trait("Category", "Evaluation")]
public sealed class AnswerEvaluationTests(ITestOutputHelper output)
{
    [BilledEvaluationFact]
    public async Task Answers_are_grounded_cited_and_refused_as_well_as_the_baseline()
    {
        await using var host = await EvaluationHost.StartAsync(withChat: true);
        await using var judgeServices = JudgeServices();
        var judge = new GroundednessJudge(judgeServices.GetRequiredService<IChatCompletionService>());

        var report = await AnswerEvaluation.RunAsync(host, EvaluationSet.LoadQuestions(), judge, EvaluationSettings.JudgeModel);

        if (EvaluationSettings.Record)
        {
            host.Embeddings.Save();
        }

        var configuration = new Dictionary<string, string>(RetrievalEvaluationTests.Configuration(host), StringComparer.Ordinal)
        {
            ["chatModel"] = host.ChatModel,
            ["temperature"] = EvaluationSettings.Configuration["Ai:Temperature"] ?? "provider default",
            ["promptTemplateVersion"] = report.Results[0].Response.PromptTemplateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["judgeModel"] = report.JudgeModel,
            ["judgePromptVersion"] = GroundednessJudge.PromptVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["judgeVotes"] = "1",
        };

        // A run with violations never becomes the baseline: the bar would include a breach.
        if (EvaluationSettings.UpdateBaseline && report.Violations.Count == 0)
        {
            Baseline.Save("answers", report.Metrics, configuration);
        }

        var comparison = Baseline.Compare("answers", report.Metrics, tolerance: 0.10);
        var path = Baseline.WriteReport("answers.md", report.ToMarkdown(comparison, RetrievalEvaluationTests.Describe(configuration)));
        output.WriteLine($"Report: {path}");
        output.WriteLine(comparison.ToMarkdown());
        foreach (var violation in report.Violations)
        {
            output.WriteLine("VIOLATION " + violation);
        }

        report.Violations.ShouldBeEmpty();
        comparison.Recorded.ShouldNotBeNull("No answer baseline is checked in. Record one: EKA_EVAL_UPDATE_BASELINE=1.");
        comparison.Regressions.ShouldBeEmpty();
    }

    /// <summary>
    /// Calibration: the judge must tell a supported answer, an over-reaching one and an
    /// unsupported one apart, or its "all supported" means nothing.
    /// </summary>
    /// <remarks>
    /// The over-reach is real: Step 10's real-stack run produced it (step-10 section 6). A judge
    /// that grades it SUPPORTED cannot catch the failure this whole measurement exists for.
    /// </remarks>
    [BilledEvaluationFact]
    public async Task The_judge_tells_supported_overreaching_and_unsupported_answers_apart()
    {
        await using var judgeServices = JudgeServices();

        // Three votes, as a single vote was measured to flip on exactly this case (see
        // GroundednessJudge). The calibration tests the judge as the full run should use it.
        var judge = new GroundednessJudge(judgeServices.GetRequiredService<IChatCompletionService>(), votes: 3);
        (int, string)[] taxis =
        [
            (1, "Taxis are reimbursed only between 22:00 and 06:00, or when no public transport is available for the journey. Keep the receipt."),
        ];
        const string Question = "Can I take a taxi home after a late meeting?";

        var supported = await judge.JudgeAsync(Question,
            "Taxis are reimbursed between 22:00 and 06:00, or when no public transport is available [1].", taxis);
        var overreach = await judge.JudgeAsync(Question,
            "Yes, you can take a taxi home after a late meeting, as taxis are reimbursed between 22:00 and 06:00 or when no public transport is available [1].", taxis);
        var unsupported = await judge.JudgeAsync(Question,
            "Yes, taxis are always reimbursed up to 50 euros per ride [1].", taxis);

        output.WriteLine($"supported:   {supported}");
        output.WriteLine($"overreach:   {overreach}");
        output.WriteLine($"unsupported: {unsupported}");

        supported.Verdict.ShouldBe(Verdict.Supported);
        overreach.Verdict.ShouldBe(Verdict.Partial);
        unsupported.Verdict.ShouldBe(Verdict.Unsupported);
    }

    /// <summary>The same adapter as production, pointed at the judge model, at temperature 0.</summary>
    private static ServiceProvider JudgeServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(EvaluationSettings.Configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:ChatModel"] = EvaluationSettings.JudgeModel,
                ["Ai:Temperature"] = "0",
                // A short claim list and two lines. A small cap also keeps the credit reserved per
                // call small.
                ["Ai:MaxOutputTokens"] = "400",
            })
            .Build();

        return new ServiceCollection().AddLogging().AddAi(configuration).BuildServiceProvider();
    }
}

/// <summary>Skips unless a key is configured and <c>EKA_EVAL_ANSWERS=1</c> asks for the billed run.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BilledEvaluationFactAttribute : FactAttribute
{
    public BilledEvaluationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EKA_EVAL_ANSWERS") is not ("1" or "true"))
        {
            Skip = "Answer evaluation is billed (about ten cents). Run it with: $env:EKA_EVAL_ANSWERS=\"1\"; "
                + "dotnet test tests/EKA.Evaluation --filter Category=Evaluation";
        }
        else if (!EvaluationSettings.HasApiKey)
        {
            Skip = "No AI provider key configured: dotnet user-secrets --project src/EKA.Api set \"Ai:ApiKey\" \"<key>\".";
        }
    }
}
