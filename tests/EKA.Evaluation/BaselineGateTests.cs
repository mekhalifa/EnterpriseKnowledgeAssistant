using EKA.Evaluation.Harness;
using Shouldly;

namespace EKA.Evaluation;

/// <summary>The gate's own rules: what counts as a regression, and what does not.</summary>
public sealed class BaselineGateTests
{
    private static Baseline.Comparison Compare(double now, double recorded, double tolerance) =>
        new(new Dictionary<string, double> { ["m"] = now }, new Dictionary<string, double> { ["m"] = recorded }, tolerance);

    [Fact]
    public void Any_drop_fails_a_deterministic_metric()
    {
        Compare(now: 0.94, recorded: 0.95, tolerance: 0).Regressions.ShouldHaveSingleItem().ShouldContain("below the baseline");
    }

    [Fact]
    public void A_drop_within_the_tolerance_passes_a_model_metric_and_one_beyond_it_fails()
    {
        Compare(now: 0.86, recorded: 0.95, tolerance: 0.10).Regressions.ShouldBeEmpty();
        Compare(now: 0.84, recorded: 0.95, tolerance: 0.10).Regressions.ShouldHaveSingleItem();
    }

    [Fact]
    public void An_improvement_passes_without_moving_the_bar()
    {
        Compare(now: 1.0, recorded: 0.9, tolerance: 0).Regressions.ShouldBeEmpty();
    }

    [Fact]
    public void A_metric_that_disappears_is_a_regression_not_a_pass()
    {
        new Baseline.Comparison(new Dictionary<string, double>(), new Dictionary<string, double> { ["hitAt1"] = 0.9 }, 0)
            .Regressions.ShouldHaveSingleItem().ShouldContain("no longer measured");
    }

    [Theory]
    [InlineData("Partial,Partial,Supported", "Partial")]
    [InlineData("Supported,Supported,Partial", "Supported")]
    [InlineData("Supported,Partial,Unsupported", "Unsupported")] // three-way tie: strictest
    [InlineData("Supported,Partial", "Partial")]                 // two-way tie: stricter
    public void The_judge_majority_breaks_ties_toward_the_stricter_verdict(string ballots, string expected)
    {
        var judgements = ballots.Split(',').Select(v => new Judgement(Enum.Parse<Verdict>(v), v)).ToList();

        GroundednessJudge.Majority(judgements).Verdict.ShouldBe(Enum.Parse<Verdict>(expected));
    }

    [Fact]
    public void A_metric_with_nothing_to_measure_scores_zero_not_one()
    {
        // The first real run reported groundedness 1.000 because every judge call had failed.
        var report = new AnswerReport([], "m", "j");

        report.Metrics["groundedness"].ShouldBe(0);
        report.Metrics["answerRate"].ShouldBe(0);
    }
}
