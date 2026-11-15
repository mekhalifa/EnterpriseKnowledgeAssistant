using System.ClientModel;
using System.ClientModel.Primitives;
using EKA.AI.Configuration;
using EKA.Application.Abstractions.AI;
using EKA.Domain.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using OpenAI;

namespace EKA.AI;

/// <summary>
/// Registers the AI adapters behind <see cref="IChatCompletionService"/> and
/// <see cref="IEmbeddingService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The call path, outermost first — each layer is a decorator around the next, which is why
/// any one can be replaced or removed without touching the others:
/// </para>
/// <code>
/// ChatCompletionService          our port: domain types in, Result out
///  └ LoggingChatClient           Microsoft.Extensions.AI middleware (UseLogging)
///     └ OpenAI IChatClient       Microsoft.Extensions.AI.OpenAI adapter over the SDK
///        └ OpenAI SDK            SDK retries DISABLED (see below)
///           └ HttpClient         from IHttpClientFactory, named "eka-ai"
///              └ resilience      total timeout → retry → circuit breaker → attempt timeout
///                 └ SocketsHttpHandler
/// </code>
/// <para>
/// <b>Exactly one retry layer.</b> The OpenAI SDK retries on its own by default, three times.
/// Stacked under the HTTP resilience handler's three, a sustained outage would make up to
/// 4 × 4 = 16 requests per call — each one a paid request if the provider accepted it before
/// failing. The SDK's retry policy is set to zero so the resilience handler, which is
/// configurable, observable and has a circuit breaker, is the only one.
/// </para>
/// <para>
/// <b>Retrying a POST.</b> Every call here is a POST, and the standard handler retries POSTs.
/// That is accepted deliberately: a chat completion or an embedding has no side effect beyond
/// cost, so a retry risks paying twice and never doing something twice. A non-idempotent
/// call (Step 12's anything-that-writes) would need <c>DisableForUnsafeHttpMethods()</c>.
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>The named <see cref="HttpClient"/> every provider call goes through. Public so
    /// tests can substitute its primary handler and exercise the real resilience pipeline.</summary>
    public const string HttpClientName = "eka-ai";

    public static IServiceCollection AddAi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // ValidateOnStart: a missing key or a zero dimension stops the host during startup.
        // Without it, options are validated lazily on first resolution — the first user's
        // first question.
        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AiOptions>, AiOptionsValidator>();

        services.AddHttpClient(HttpClientName)
            // The resilience pipeline owns every timeout. HttpClient's own 100-second default
            // would otherwise sit above it and cut off a legitimate retry sequence.
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            // The OpenAI client is a singleton and keeps this HttpClient for the life of the
            // process, so IHttpClientFactory's handler rotation never happens. Rotation exists
            // to pick up DNS changes; PooledConnectionLifetime does the same job from inside a
            // long-lived handler, which is the documented pattern for this case.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .AddStandardResilienceHandler()
            .Configure(resilience =>
            {
                // Bound straight from configuration, NOT read through IOptions<AiOptions>.
                // The resilience package validates its own options on start; resolving the
                // validated AiOptions from inside that callback made every AiOptions failure
                // surface twice in one AggregateException (step-05 §5.1). AiOptionsValidator
                // still checks these same values, once.
                var tuning = configuration
                    .GetSection($"{AiOptions.SectionName}:{nameof(AiOptions.Resilience)}")
                    .Get<AiResilienceOptions>() ?? new AiResilienceOptions();

                resilience.TotalRequestTimeout.Timeout = tuning.TotalTimeout;
                resilience.AttemptTimeout.Timeout = tuning.AttemptTimeout;

                resilience.Retry.MaxRetryAttempts = tuning.MaxRetryAttempts;
                resilience.Retry.Delay = tuning.RetryBaseDelay;
                resilience.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
                // Jitter spreads retries from many instances so they do not arrive at the
                // provider in synchronized waves right as it recovers.
                resilience.Retry.UseJitter = true;

                // The library requires the sampling window to be at least twice the attempt
                // timeout — otherwise a single slow attempt could straddle the whole window.
                resilience.CircuitBreaker.SamplingDuration = tuning.AttemptTimeout * 2;
                resilience.CircuitBreaker.BreakDuration = tuning.CircuitBreakDuration;
            });

        services.AddSingleton(CreateOpenAiClient);

        // AddChatClient / AddEmbeddingGenerator return a builder: each Use…() wraps the
        // client in one more decorator. UseLogging logs calls at Debug and message content
        // only at Trace — leave Trace off anywhere real documents flow, because at Trace the
        // log becomes a copy of every prompt and answer. UseOpenTelemetry joins in Step 14.
        services
            .AddChatClient(serviceProvider =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<AiOptions>>().Value;
                return serviceProvider.GetRequiredService<OpenAIClient>()
                    .GetChatClient(options.ChatModel)
                    .AsIChatClient();
            })
            .UseLogging();

        services
            .AddEmbeddingGenerator(serviceProvider =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<AiOptions>>().Value;
                return serviceProvider.GetRequiredService<OpenAIClient>()
                    .GetEmbeddingClient(options.EmbeddingModel)
                    .AsIEmbeddingGenerator(options.EmbeddingDimensions);
            })
            .UseLogging();

        services.AddSingleton<IChatCompletionService, ChatCompletionService>();
        services.AddSingleton<IEmbeddingService, EmbeddingService>();

        return services;
    }

    /// <summary>
    /// The Factory: provider kind in, configured SDK client out. Adding Azure OpenAI is a new
    /// case here and a new client type — nothing above this method changes.
    /// </summary>
    private static OpenAIClient CreateOpenAiClient(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetRequiredService<IOptions<AiOptions>>().Value;
        var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);

        var clientOptions = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(httpClient),
            // See the class remarks: one retry layer, and it is not this one.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            // Slightly beyond the resilience pipeline's total timeout, so the pipeline —
            // which knows about retries — is always the one that decides to give up.
            NetworkTimeout = options.Resilience.TotalTimeout + TimeSpan.FromSeconds(5),
        };

        switch (options.Kind)
        {
            case AIProviderKind.OpenAI:
                // Fixed public host; an Endpoint, if configured, still wins.
                if (options.Endpoint is not null)
                {
                    clientOptions.Endpoint = options.Endpoint;
                }

                break;

            case AIProviderKind.OpenAICompatible:
                clientOptions.Endpoint = options.Endpoint;
                break;

            default:
                // Unreachable after AiOptionsValidator, but a factory that silently builds the
                // wrong client for an unknown kind would send the key to the wrong vendor.
                throw new NotSupportedException($"AI provider kind '{options.Kind}' has no adapter.");
        }

        return new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions);
    }
}
