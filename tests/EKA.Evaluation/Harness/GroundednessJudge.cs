using System.Text;
using System.Text.RegularExpressions;
using EKA.Application.Abstractions.AI;
using EKA.Domain.Conversations;

namespace EKA.Evaluation.Harness;

internal enum Verdict
{
    Supported,
    Partial,
    Unsupported,
    Unparseable,
}

internal sealed record Judgement(Verdict Verdict, string Reason)
{
    /// <summary>1, 0.5, 0. Null for an unparseable reply, which is excluded from the mean and
    /// counted separately, never silently scored as a pass or a fail.</summary>
    public double? Score => Verdict switch
    {
        Verdict.Supported => 1,
        Verdict.Partial => 0.5,
        Verdict.Unsupported => 0,
        _ => null,
    };
}

/// <summary>
/// LLM-as-judge for one property only: does the cited text support the answer?
/// </summary>
/// <remarks>
/// <para>
/// <b>Why only this.</b> Everything that can be checked exactly is checked exactly: whether it
/// refused, whether it cited the expected section, whether a canary leaked. Groundedness cannot
/// be: "taxis are reimbursed 22:00 to 06:00" against "yes, you can take a taxi after a late
/// meeting" is a judgement about meaning. That is what a judge model is for, and the only thing
/// it is used for.
/// </para>
/// <para>
/// <b>Its limits, which the report should be read with.</b> A judge has biases of its own: it
/// prefers longer answers, and it tends to agree with output in its own style. So it is a
/// different, stronger model from the one answering (<c>Evaluation:JudgeModel</c>, default
/// <c>openai/gpt-4o</c>), runs at temperature 0, sees only the cited sources (not the whole
/// corpus), and gives a reason with every verdict so a person can audit a sample. The prompt
/// is versioned (<see cref="PromptVersion"/>): changing it changes every score, exactly like the
/// answering prompt (ADR-0019).
/// </para>
/// </remarks>
internal sealed partial class GroundednessJudge(IChatCompletionService judge, int votes = 1)
{
    /// <summary>
    /// Asks <paramref name="votes"/> times and takes the majority; a tie goes to the stricter
    /// verdict.
    /// </summary>
    /// <remarks>
    /// Measured in Step 11: at temperature 0, judge v2 graded a borderline over-reach PARTIAL in
    /// about four of five samples and SUPPORTED in the fifth. Single votes flip toward leniency,
    /// which hides regressions rather than inventing them. Three votes make a flip need two
    /// coincidences, at three times the judge cost. Any unparseable vote makes the whole
    /// judgement unparseable: a partly failed instrument is a failed instrument.
    /// </remarks>
    public async Task<Judgement> JudgeAsync(string question, string answer, IReadOnlyList<(int Number, string Text)> sources, CancellationToken cancellationToken = default)
    {
        var ballots = new List<Judgement>(votes);
        for (var i = 0; i < Math.Max(1, votes); i++)
        {
            var ballot = await JudgeOnceAsync(question, answer, sources, cancellationToken).ConfigureAwait(false);
            if (ballot.Verdict is Verdict.Unparseable)
            {
                return ballot;
            }

            ballots.Add(ballot);
        }

        return Majority(ballots);
    }

    /// <summary>Most frequent verdict; ties to the stricter one (Unsupported over Partial over Supported).</summary>
    internal static Judgement Majority(IReadOnlyList<Judgement> ballots) =>
        ballots
            .GroupBy(ballot => ballot.Verdict)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key) // enum order: Supported < Partial < Unsupported
            .First()
            .First();

    /// <summary>
    /// v2 (Step 11): v1 graded "Yes, you can take a taxi after a late meeting, as taxis are
    /// reimbursed 22:00 to 06:00" as SUPPORTED. It saw the condition restated and missed that
    /// the leading "yes" is unconditional. The calibration test caught it. v2 makes the judge
    /// list the claims, the direct answer first, and check each against its conditions before
    /// it may give a verdict.
    /// </summary>
    public const int PromptVersion = 2;

    private const string Instruction =
        "You are a strict fact-checker for an enterprise knowledge assistant.\n"
        + "You receive SOURCES (excerpts from company documents), a QUESTION, and an ANSWER that cites the sources with markers like [1].\n"
        + "Decide whether the ANSWER is supported by the SOURCES. Judge only against the sources, never against your own knowledge.\n"
        + "\n"
        + "First list every claim the answer makes, one per line, starting with its direct answer to the question "
        + "(a yes, a no, a number, a rule) exactly as strongly as the answer states it. For each claim, say whether the sources "
        + "state it with the same strength. A claim that is unconditional where the sources attach a condition, a limit or an "
        + "exception is NOT supported, even if the condition is mentioned elsewhere in the answer: \"Yes, you can\" followed by "
        + "the conditions is still an unconditional yes.\n"
        + "\n"
        + "Then decide:\n"
        + "- SUPPORTED: every claim is stated in, or directly follows from, the sources, with the same strength.\n"
        + "- PARTIAL: the core facts are in the sources, but at least one claim adds, generalises, overstates, or drops a condition.\n"
        + "- UNSUPPORTED: the main claim is not in the sources, or contradicts them.\n"
        + "Citation markers are not claims. A statement that the documents do not cover something is not a claim.\n"
        + "\n"
        + "Reply in exactly this format:\n"
        + "CLAIMS:\n"
        + "- <claim>: <supported | overstated | missing>\n"
        + "VERDICT: SUPPORTED | PARTIAL | UNSUPPORTED\n"
        + "REASON: <one sentence>";

    private async Task<Judgement> JudgeOnceAsync(string question, string answer, IReadOnlyList<(int Number, string Text)> sources, CancellationToken cancellationToken)
    {
        var user = new StringBuilder("SOURCES:\n");
        foreach (var (number, text) in sources)
        {
            user.Append('[').Append(number).Append("] ").AppendLine(text.Trim()).AppendLine();
        }

        user.Append("QUESTION: ").AppendLine(question).AppendLine();
        user.Append("ANSWER: ").Append(answer);

        var reply = await judge.CompleteAsync(
            [new ChatTurn(MessageRole.System, Instruction), new ChatTurn(MessageRole.User, user.ToString())],
            cancellationToken).ConfigureAwait(false);

        if (reply.IsFailure)
        {
            return new Judgement(Verdict.Unparseable, $"judge call failed: {reply.Error.Code}: {reply.Error.Message}");
        }

        var verdict = VerdictLine().Match(reply.Value.Content);
        var reason = ReasonLine().Match(reply.Value.Content);

        return verdict.Success
            ? new Judgement(
                verdict.Groups["verdict"].Value.ToUpperInvariant() switch
                {
                    "SUPPORTED" => Verdict.Supported,
                    "PARTIAL" => Verdict.Partial,
                    _ => Verdict.Unsupported,
                },
                reason.Success ? reason.Groups["reason"].Value.Trim() : string.Empty)
            : new Judgement(Verdict.Unparseable, reply.Value.Content.ReplaceLineEndings(" "));
    }

    [GeneratedRegex(@"VERDICT:\s*(?<verdict>SUPPORTED|PARTIAL|UNSUPPORTED)", RegexOptions.IgnoreCase)]
    private static partial Regex VerdictLine();

    [GeneratedRegex(@"REASON:\s*(?<reason>.+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReasonLine();
}
