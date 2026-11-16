using EKA.Application.Abstractions.AI;
using EKA.Application.Conversations;
using EKA.Application.Conversations.AskQuestion;
using EKA.Application.Conversations.GetConversation;
using EKA.Application.Conversations.ListConversations;
using EKA.Application.Conversations.StartConversation;
using EKA.Application.Documents.DeleteDocument;
using EKA.Application.Identity;
using EKA.Application.Documents.GetDocument;
using EKA.Application.Documents.ListDocuments;
using EKA.Application.Documents.ExtractDocumentText;
using EKA.Application.Documents.IndexDocument;
using EKA.Application.Documents.Indexing;
using EKA.Application.Documents.Ingestion;
using EKA.Application.Documents.RecoverStalledDocuments;
using EKA.Application.Documents.UploadDocument;
using EKA.Application.Documents.RenameDocument;
using EKA.Application.Documents.ResetDocument;
using EKA.Application.Documents.SetDocumentAccess;
using EKA.Application.Retrieval;
using EKA.Application.Retrieval.SearchChunks;
using EKA.Application.Usage;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EKA.Application;

/// <summary>
/// Registers the use-case handlers and their validators.
/// </summary>
/// <remarks>
/// <para>
/// <b>Handlers are listed by hand, not scanned for.</b> Assembly scanning would be three
/// lines instead of six, and it would also mean that deleting a handler still compiles, a
/// typo in a namespace silently unregisters one, and the only way to answer "what does this
/// application do?" is to run it. The list below is that answer, and it fails at compile
/// time when it goes stale.
/// </para>
/// <para>
/// <b>Validators are scanned for</b>, because the opposite trade-off applies: a validator
/// that is never resolved does not break the build, it just stops validating, and a missing
/// registration is invisible until a bad request gets through. Scanning means adding a
/// validator is enough to make it run. <c>includeInternalTypes</c> is off: only public
/// validators are registered, so an internal helper cannot accidentally become a rule.
/// </para>
/// <para>
/// <b>Scoped, not transient or singleton.</b> Handlers hold a repository and a unit of work
/// that are themselves scoped to the request's <c>DbContext</c>; registering a handler as a
/// singleton would capture that <c>DbContext</c> forever, which is the classic captive
/// dependency bug — it works in development and corrupts state under concurrency.
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// What every host needs: the clock, the option rules, and the validators.
    /// </summary>
    /// <remarks>
    /// <b>Split into three since Step 7.</b> One method that registered every handler made
    /// every host carry every handler's dependencies. The worker has no chat client, so in
    /// Development, where the host validates the whole graph at build time,
    /// <c>AskQuestionHandler</c>'s missing <c>IChatCompletionService</c> stopped the worker
    /// from starting at all (step-07 section 5.4). Each host now registers what it runs:
    /// <see cref="AddRequestHandlers"/> for the API, <see cref="AddIngestionHandlers"/> for
    /// the worker.
    /// </remarks>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The single place "now" enters the system. Registered rather than called
        // statically so a test can substitute a FakeTimeProvider and assert on exact
        // instants — the application-layer half of ADR-0008.
        services.AddSingleton(TimeProvider.System);

        // Option rules. The host binds each section; this layer contributes only the rules,
        // validated at startup.
        services.AddSingleton<IValidateOptions<ChatOptions>, ChatOptionsValidator>();
        services.AddSingleton<IValidateOptions<IngestionOptions>, IngestionOptionsValidator>();
        services.AddSingleton<IValidateOptions<IndexingOptions>, IndexingOptionsValidator>();
        services.AddSingleton<IValidateOptions<RetrievalOptions>, RetrievalOptionsValidator>();
        services.AddSingleton<IValidateOptions<TokenBudgetOptions>, TokenBudgetOptionsValidator>();

        // Both hosts spend tokens: the API on questions, the worker on ingestion (Step 12).
        services.AddScoped<TokenBudget>();

        // Pure and stateless, and needed by both hosts since Step 8: the API budgets prompts
        // with it, the worker sizes chunks with it. One counter for both means a chunk the
        // worker sized at 500 tokens costs 500 tokens when the API puts it in a prompt.
        // Replaced by a real tokenizer in EKA.AI if Step 11 shows the estimate is not
        // conservative enough (see ITokenCounter).
        services.AddSingleton<ITokenCounter, EstimatingTokenCounter>();

        // Taken from the marker's assembly rather than `...Containing<TMarker>()`, which
        // cannot accept a static class as a type argument.
        services.AddValidatorsFromAssembly(
            typeof(ApplicationAssemblyMarker).Assembly,
            lifetime: ServiceLifetime.Singleton,
            includeInternalTypes: false);

        return services;
    }

    /// <summary>The use cases behind HTTP endpoints. Registered by <c>EKA.Api</c>.</summary>
    public static IServiceCollection AddRequestHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<UploadDocumentHandler>();
        services.AddScoped<GetDocumentHandler>();
        services.AddScoped<ListDocumentsHandler>();
        services.AddScoped<RenameDocumentHandler>();
        services.AddScoped<ResetDocumentHandler>();
        services.AddScoped<DeleteDocumentHandler>();
        services.AddScoped<SetDocumentAccessHandler>();

        // Retrieval (Step 9). The retriever is its own registration because Step 10's
        // answer generation will use it too, not only the search endpoint.
        services.AddScoped<ChunkRetriever>();
        services.AddScoped<SearchChunksHandler>();

        services.AddScoped<GetUsageHandler>();

        services.AddScoped<StartConversationHandler>();
        services.AddScoped<AskQuestionHandler>();
        services.AddScoped<GetConversationHandler>();
        services.AddScoped<ListConversationsHandler>();

        services.AddScoped<UserProvisioner>();

        // Transient, not scoped: the builder accumulates parts in fields. Sharing one instance
        // across two prompts built in the same request would leak the first prompt's history
        // into the second.
        services.AddTransient<PromptBuilder>();

        return services;
    }

    /// <summary>The background use cases. Registered by <c>EKA.Ingestion.Worker</c>.</summary>
    public static IServiceCollection AddIngestionHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ExtractDocumentTextHandler>();
        services.AddScoped<RecoverStalledDocumentsHandler>();
        services.AddScoped<TextExtractionPipeline>();
        services.AddScoped<ExtractedTextStore>();

        services.AddScoped<IndexDocumentHandler>();

        // The chunking Strategy. Swapping the algorithm (Step 11) is this one line.
        services.AddSingleton<IChunkingStrategy, SectionAwareChunker>();

        return services;
    }
}
