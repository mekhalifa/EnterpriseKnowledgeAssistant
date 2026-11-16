using System.Text.Json;
using System.Text.Json.Serialization;

namespace EKA.Evaluation.Harness;

/// <summary>What a question is testing, and therefore how its result is judged.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QuestionKind>))]
internal enum QuestionKind
{
    [JsonStringEnumMemberName("answerable")] Answerable,
    [JsonStringEnumMemberName("follow_up")] FollowUp,
    [JsonStringEnumMemberName("injection")] Injection,
    [JsonStringEnumMemberName("restricted")] Restricted,
    [JsonStringEnumMemberName("unanswerable")] Unanswerable,
    [JsonStringEnumMemberName("off_topic")] OffTopic,
}

internal sealed record ExpectedSource(string Document, string Section);

internal sealed record EvaluationQuestion(
    string Id,
    QuestionKind Kind,
    string Caller,
    string Question,
    IReadOnlyList<ExpectedSource>? Expected = null,
    IReadOnlyList<string>? Forbidden = null,
    IReadOnlyList<string>? History = null)
{
    /// <summary>Should be answered, citing an expected source.</summary>
    public bool ExpectsAnswer => Kind is QuestionKind.Answerable or QuestionKind.FollowUp or QuestionKind.Injection;

    /// <summary>Must not be answered with a citation.</summary>
    public bool MustRefuse => Kind is QuestionKind.Restricted or QuestionKind.Unanswerable or QuestionKind.OffTopic;

    public IReadOnlyList<ExpectedSource> ExpectedSources => Expected ?? [];

    public IReadOnlyList<string> ForbiddenText => Forbidden ?? [];
}

internal sealed record CorpusDocument(string Key, string File, string Title, IReadOnlyList<string> Access);

internal sealed record CorpusManifest(IReadOnlyList<CorpusDocument> Documents, IReadOnlyDictionary<string, IReadOnlyList<string>> Callers);

internal sealed record QuestionFile(IReadOnlyList<EvaluationQuestion> Questions);

internal static class EvaluationSet
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static CorpusManifest LoadCorpus() =>
        JsonSerializer.Deserialize<CorpusManifest>(File.ReadAllText(Path.Combine(EvaluationSettings.CorpusDirectory, "corpus.json")), Json)
        ?? throw new InvalidOperationException("corpus.json is empty.");

    public static IReadOnlyList<EvaluationQuestion> LoadQuestions()
    {
        var questions = (JsonSerializer.Deserialize<QuestionFile>(File.ReadAllText(EvaluationSettings.QuestionsFile), Json)
            ?? throw new InvalidOperationException("questions.json is empty.")).Questions;

        // A malformed set would quietly score nothing and pass, so it is checked first.
        var duplicate = questions.GroupBy(question => question.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Question id {duplicate.Key} appears twice.");
        }

        var missing = questions.FirstOrDefault(question => question.ExpectsAnswer && question.ExpectedSources.Count == 0);
        if (missing is not null)
        {
            throw new InvalidOperationException($"Question {missing.Id} expects an answer but names no expected source.");
        }

        return questions;
    }
}
