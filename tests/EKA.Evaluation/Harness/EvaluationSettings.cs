using Microsoft.Extensions.Configuration;

namespace EKA.Evaluation.Harness;

/// <summary>
/// Where the evaluation's files live, and the configuration it measures.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shipped configuration, not a copy.</b> Settings come from
/// <c>src/EKA.Api/appsettings.json</c> (retrieval, chat, AI models), from
/// <c>src/EKA.Ingestion.Worker/appsettings.json</c> (chunking), then the API's User Secrets
/// (the key), then environment variables. A score therefore describes the system as
/// configured, and changing a setting is visible in the next run.
/// </para>
/// <para>
/// <b>Two switches, both off by default:</b>
/// </para>
/// <list type="bullet">
/// <item><description><c>EKA_EVAL_RECORD=1</c>: embedding-cache misses call the real embedding
/// model and are written back to <c>Cache/embeddings.json</c>. Needed after changing the corpus,
/// the questions, the chunker, or the embedding model. Billed, fractions of a cent.</description></item>
/// <item><description><c>EKA_EVAL_UPDATE_BASELINE=1</c>: the run's metrics become the new
/// baseline, and the reports are written to <c>docs/evaluation/</c>. A deliberate act, reviewed in
/// the diff like any code change.</description></item>
/// </list>
/// </remarks>
internal static class EvaluationSettings
{
    /// <summary>Must equal &lt;UserSecretsId&gt; in src/EKA.Api/EKA.Api.csproj.</summary>
    private const string ApiUserSecretsId = "eka-api-6f3b2a10-5d41-4c8e-9a77-21b0c9e4d5f3";

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string ProjectDirectory => Path.Combine(RepositoryRoot, "tests", "EKA.Evaluation");

    public static string CorpusDirectory => Path.Combine(ProjectDirectory, "Corpus");

    public static string QuestionsFile => Path.Combine(ProjectDirectory, "Questions", "questions.json");

    public static string EmbeddingCacheFile => Path.Combine(ProjectDirectory, "Cache", "embeddings.json");

    public static string BaselineDirectory => Path.Combine(ProjectDirectory, "Baselines");

    public static string DocsDirectory => Path.Combine(RepositoryRoot, "docs", "evaluation");

    /// <summary>Reports of every run land here (ignored by git, under bin).</summary>
    public static string RunOutputDirectory => Path.Combine(AppContext.BaseDirectory, "evaluation");

    public static bool Record => Flag("EKA_EVAL_RECORD");

    public static bool UpdateBaseline => Flag("EKA_EVAL_UPDATE_BASELINE");

    public static IConfiguration Configuration { get; } = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(RepositoryRoot, "src", "EKA.Api", "appsettings.json"), optional: false)
        .AddJsonFile(Path.Combine(RepositoryRoot, "src", "EKA.Ingestion.Worker", "appsettings.json"), optional: false)
        .AddUserSecrets(ApiUserSecretsId)
        .AddEnvironmentVariables()
        .Build();

    public static bool HasApiKey => !string.IsNullOrWhiteSpace(Configuration["Ai:ApiKey"]);

    /// <summary>The model that grades groundedness. Deliberately not the answering model: a model
    /// grading its own output tends to agree with it.</summary>
    public static string JudgeModel => Configuration["Evaluation:JudgeModel"] ?? "openai/gpt-4o";

    private static bool Flag(string name) =>
        Environment.GetEnvironmentVariable(name) is "1" or "true" or "TRUE" or "True";

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EnterpriseKnowledgeAssistant.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
