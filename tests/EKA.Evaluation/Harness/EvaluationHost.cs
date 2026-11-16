using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EKA.AI;
using EKA.Application;
using EKA.Application.Abstractions;
using EKA.Application.Abstractions.AI;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Abstractions.Retrieval;
using EKA.Application.Conversations;
using EKA.Application.Conversations.AskQuestion;
using EKA.Application.Conversations.StartConversation;
using EKA.Application.Documents.ExtractDocumentText;
using EKA.Application.Documents.Indexing;
using EKA.Application.Documents.IndexDocument;
using EKA.Application.Documents.Ingestion;
using EKA.Application.Identity;
using EKA.Application.Retrieval;
using EKA.Contracts.Conversations;
using EKA.Domain.Access;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using EKA.Domain.Prompting;
using EKA.Infrastructure;
using GroundedAnswers = EKA.Infrastructure.Persistence.Migrations.GroundedAnswers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EKA.Evaluation.Harness;

/// <summary>
/// The system under evaluation: the real use cases, composed as the hosts compose them, over
/// in-memory stores, with the corpus ingested through the real extraction and indexing handlers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the real handlers.</b> An evaluation that re-implemented chunking or prompt building
/// would score the re-implementation. Here the corpus goes through <c>ExtractDocumentTextHandler</c>
/// and <c>IndexDocumentHandler</c>, questions through <c>ChunkRetriever</c> and
/// <c>AskQuestionHandler</c>, and the prompt is the v2 text the migration seeds. What is
/// replaced is storage (in memory) and, for retrieval, the network (recorded embeddings).
/// </para>
/// </remarks>
internal sealed class EvaluationHost : IAsyncDisposable
{
    public static readonly TenantId Tenant = new(Guid.Parse("e7a1e7a1-0000-4000-8000-000000000011"));

    private readonly ServiceProvider _services;
    private readonly ServiceProvider? _providers;

    private EvaluationHost(
        ServiceProvider services,
        ServiceProvider? providers,
        CachedEmbeddingService embeddings,
        RecordingChatService? chat,
        CorpusManifest corpus)
    {
        _services = services;
        _providers = providers;
        Embeddings = embeddings;
        Chat = chat;
        Corpus = corpus;
    }

    public CachedEmbeddingService Embeddings { get; }

    /// <summary>The answering model, recording every prompt. Null in the retrieval-only tier.</summary>
    public RecordingChatService? Chat { get; }

    public CorpusManifest Corpus { get; }

    /// <summary>Corpus key → the ingested document.</summary>
    public IReadOnlyDictionary<string, Document> Documents { get; private set; } = new Dictionary<string, Document>();

    public RetrievalOptions RetrievalOptions => _services.GetRequiredService<IOptions<RetrievalOptions>>().Value;

    public IndexingOptions IndexingOptions => _services.GetRequiredService<IOptions<IndexingOptions>>().Value;

    public string ChatModel => EvaluationSettings.Configuration["Ai:ChatModel"] ?? "unknown";

    /// <param name="withChat">True for the answer tier: the real chat model is wired in, and a
    /// key is required. False for the retrieval tier, which needs no network unless recording.</param>
    public static async Task<EvaluationHost> StartAsync(bool withChat)
    {
        var configuration = EvaluationSettings.Configuration;
        var corpus = EvaluationSet.LoadCorpus();

        // The real provider adapters, only when something may call them.
        ServiceProvider? providers = null;
        if (withChat || EvaluationSettings.Record)
        {
            providers = new ServiceCollection().AddLogging().AddAi(configuration).BuildServiceProvider();
        }

        var embeddings = new CachedEmbeddingService(
            configuration["Ai:EmbeddingModel"] ?? throw new InvalidOperationException("Ai:EmbeddingModel is not configured."),
            configuration.GetValue<int>("Ai:EmbeddingDimensions"),
            EvaluationSettings.Record ? providers!.GetRequiredService<IEmbeddingService>() : null);

        var chat = withChat ? new RecordingChatService(providers!.GetRequiredService<IChatCompletionService>()) : null;

        var services = new ServiceCollection().AddLogging();
        services.AddApplication().AddRequestHandlers().AddIngestionHandlers();
        services.AddTextExtraction();

        // The shipped tuning, read from the shipped appsettings files.
        services.AddOptions<IndexingOptions>().Bind(configuration.GetSection(IndexingOptions.SectionName));
        services.AddOptions<RetrievalOptions>().Bind(configuration.GetSection(RetrievalOptions.SectionName));
        services.AddOptions<ChatOptions>().Bind(configuration.GetSection(ChatOptions.SectionName));
        services.AddOptions<IngestionOptions>().Bind(configuration.GetSection(IngestionOptions.SectionName));

        var documents = new InMemoryDocumentRepository();
        services.AddSingleton(documents);
        services.AddSingleton<IDocumentRepository>(documents);
        services.AddSingleton<IChunkReader>(new InMemoryChunkReader(documents));
        services.AddSingleton<IVectorStore, InMemoryVectorStore>();
        services.AddSingleton<IFileStorage, InMemoryFileStorage>();
        services.AddSingleton<IUserRepository, InMemoryUserRepository>();
        services.AddSingleton<IConversationRepository, InMemoryConversationRepository>();
        services.AddSingleton<IUnitOfWork, InMemoryUnitOfWork>();
        services.AddSingleton<IIngestionQueue, NoIngestionQueue>();
        services.AddSingleton<IMalwareScanner, CleanScanner>();

        // An unlimited, in-memory ledger: the evaluation measures quality, not budgets.
        services.AddSingleton<Application.Abstractions.Usage.ITokenUsageLedger, InMemoryTokenUsageLedger>();
        services.AddOptions<Application.Usage.TokenBudgetOptions>().Configure(budget => budget.MonthlyTokens = long.MaxValue);
        services.AddSingleton<IEmbeddingService>(embeddings);
        if (chat is not null)
        {
            services.AddSingleton<IChatCompletionService>(chat);
        }

        // The prompt that ships: the text the GroundedAnswers migration seeds as default v2.
        var templates = new InMemoryPromptTemplateRepository();
        var template = new PromptTemplate(
            Guid.NewGuid(), "default", 2, GroundedAnswers.SystemPrompt, GroundedAnswers.UserPromptTemplate, Guid.NewGuid(), DateTimeOffset.UtcNow);
        template.Activate(DateTimeOffset.UtcNow);
        templates.Add(template);
        services.AddSingleton<IPromptTemplateRepository>(templates);

        var host = new EvaluationHost(services.BuildServiceProvider(), providers, embeddings, chat, corpus);
        await host.IngestAsync().ConfigureAwait(false);
        return host;
    }

    public AccessScope Scope(string caller) => AccessScope.For(Tenant, SubjectOf(caller), Corpus.Callers[caller]);

    /// <summary>Retrieval exactly as production does it: shipped options, shipped retriever.</summary>
    public Task<Application.Common.Result<IReadOnlyList<RetrievedChunk>>> RetrieveAsync(string caller, string question) =>
        Retriever(RetrievalOptions).RetrieveAsync(Scope(caller), question);

    /// <summary>Everything above a zero floor, up to 50 deep: the raw material for the floor sweep.</summary>
    public Task<Application.Common.Result<IReadOnlyList<RetrievedChunk>>> RetrieveUnfilteredAsync(string caller, string question) =>
        Retriever(new RetrievalOptions { DefaultTopK = 50, MaxTopK = 50, MinimumScore = 0, CandidateMultiplier = 1 })
            .RetrieveAsync(Scope(caller), question);

    /// <summary>
    /// Asks <paramref name="question"/> in a fresh conversation, after its history questions,
    /// through the real <c>AskQuestionHandler</c>. Returns the response and the prompt the model
    /// received for the evaluated question (null if no model was called).
    /// </summary>
    public async Task<(AskQuestionResponse Response, IReadOnlyList<ChatTurn>? Prompt)> AskAsync(EvaluationQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var chat = Chat ?? throw new InvalidOperationException("This host was started without a chat model.");

        // One person per question, so conversations and users never mix between questions.
        var caller = new CallerIdentity(
            Tenant, $"{SubjectOf(question.Caller)}-{question.Id}", $"{question.Id}@eval.invalid", question.Id, Corpus.Callers[question.Caller]);

        await using var scope = _services.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<StartConversationHandler>()
            .HandleAsync(new StartConversationCommand(caller)).ConfigureAwait(false);
        var ask = scope.ServiceProvider.GetRequiredService<AskQuestionHandler>();

        foreach (var earlier in question.History ?? [])
        {
            var history = await ask.HandleAsync(new AskQuestionCommand(caller, started.Value.Id, earlier)).ConfigureAwait(false);
            if (history.IsFailure)
            {
                throw new InvalidOperationException($"{question.Id}: history question failed: {history.Error}");
            }
        }

        var promptsBefore = chat.Prompts.Count;
        var result = await ask.HandleAsync(new AskQuestionCommand(caller, started.Value.Id, question.Question)).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"{question.Id}: {result.Error}");
        }

        return (result.Value, chat.Prompts.Count > promptsBefore ? chat.Prompts[^1] : null);
    }

    /// <summary>The chunk text behind a citation, for the judge.</summary>
    public string PassageText(Guid documentId, int chunkIndex) =>
        Documents.Values.Single(d => d.Id == documentId).Chunks.Single(c => c.Index.Value == chunkIndex).Content;

    /// <summary>Corpus key of a document id.</summary>
    public string KeyOf(Guid documentId) => Documents.Single(pair => pair.Value.Id == documentId).Key;

    public bool Matches(Guid documentId, string? sectionPath, ExpectedSource expected) =>
        Documents.TryGetValue(expected.Document, out var document)
        && document.Id == documentId
        && sectionPath is not null
        && (sectionPath == expected.Section || sectionPath.EndsWith(" / " + expected.Section, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync().ConfigureAwait(false);
        if (_providers is not null)
        {
            await _providers.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string SubjectOf(string caller) => $"eval-{caller}";

    private ChunkRetriever Retriever(RetrievalOptions options) => new(
        Embeddings,
        _services.GetRequiredService<IVectorStore>(),
        _services.GetRequiredService<IChunkReader>(),
        _services.GetRequiredService<Application.Usage.TokenBudget>(),
        Options.Create(options),
        NullLogger<ChunkRetriever>.Instance);

    private async Task IngestAsync()
    {
        var repository = _services.GetRequiredService<InMemoryDocumentRepository>();
        var storage = _services.GetRequiredService<IFileStorage>();
        var ingested = new Dictionary<string, Document>();
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in Corpus.Documents)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(EvaluationSettings.CorpusDirectory, entry.File)).ConfigureAwait(false);

            // Ids derived from the corpus key: stable across runs, so tie-breaks and reports are too.
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("eka-eval:" + entry.Key)).AsSpan(0, 16));
            var document = new Document(
                id,
                Tenant,
                new DocumentTitle(entry.Title),
                new FileMetadata(entry.File, FileTypeDetector.ContentTypeOf(DocumentFileType.Markdown), bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))),
                $"eval/{entry.File}",
                now,
                access: DocumentAccess.Of(entry.Access.Select(AccessPrincipal.Parse)));

            repository.Add(document);
            using (var content = new MemoryStream(bytes))
            {
                await storage.SaveAsync(document.StorageKey, content).ConfigureAwait(false);
            }

            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ExtractDocumentTextHandler>()
                .HandleAsync(new ExtractDocumentTextCommand(Tenant, id)).ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<IndexDocumentHandler>()
                .HandleAsync(new IndexDocumentCommand(Tenant, id)).ConfigureAwait(false);

            if (document.Status is not IngestionStatus.Completed)
            {
                throw new InvalidOperationException($"Corpus document {entry.Key} ended {document.Status}: {document.FailureReason}");
            }

            ingested[entry.Key] = document;
        }

        Documents = ingested;
    }
}

/// <summary>The answering model, recording every prompt it is sent.</summary>
internal sealed class RecordingChatService(IChatCompletionService inner) : IChatCompletionService
{
    private readonly ConcurrentQueue<IReadOnlyList<ChatTurn>> _prompts = new();

    public IReadOnlyList<IReadOnlyList<ChatTurn>> Prompts => [.. _prompts];

    public Task<Application.Common.Result<ChatCompletion>> CompleteAsync(IReadOnlyList<ChatTurn> conversation, CancellationToken cancellationToken = default)
    {
        _prompts.Enqueue(conversation);
        return inner.CompleteAsync(conversation, cancellationToken);
    }
}
