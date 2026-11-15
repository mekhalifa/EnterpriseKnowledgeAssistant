using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EKA.AI.Tests.Support;

/// <summary>
/// Builds the container exactly as <c>EKA.Api</c> does — through <c>AddAi</c> — and then
/// swaps the one thing a unit test must not have: the network.
/// </summary>
internal static class AiTestHost
{
    public const int Dimensions = 4;

    public static Dictionary<string, string?> ValidSettings() => new()
    {
        ["Ai:Kind"] = "OpenAICompatible",
        ["Ai:Endpoint"] = "https://ai.example.test/v1",
        ["Ai:ApiKey"] = "test-key",
        ["Ai:ChatModel"] = "test-chat",
        ["Ai:EmbeddingModel"] = "test-embed",
        ["Ai:EmbeddingDimensions"] = Dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture),
        // Zero backoff keeps the retry tests fast without changing which requests are retried.
        ["Ai:Resilience:RetryBaseDelay"] = "00:00:00",
        ["Ai:Resilience:MaxRetryAttempts"] = "3",
        ["Ai:Resilience:AttemptTimeout"] = "00:00:00.300",
        ["Ai:Resilience:TotalTimeout"] = "00:00:05",
        ["Ai:Resilience:CircuitBreakDuration"] = "00:00:01",
    };

    public static ServiceProvider Build(
        ScriptedHandler handler,
        Action<Dictionary<string, string?>>? adjust = null)
    {
        var settings = ValidSettings();
        adjust?.Invoke(settings);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAi(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        // A second ConfigurePrimaryHttpMessageHandler on the same name runs after the one in
        // AddAi and replaces its SocketsHttpHandler. The resilience handlers stay in place.
        services.AddHttpClient(DependencyInjection.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    /// <summary>
    /// Runs what the host runs at boot for <c>ValidateOnStart()</c> registrations — the same
    /// code path that stops <c>EKA.Api</c> from starting.
    /// </summary>
    public static void ValidateLikeStartup(Action<Dictionary<string, string?>> adjust)
    {
        using var provider = Build(new ScriptedHandler(), adjust);
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    /// <summary>
    /// Every validation message startup would report. The host throws a single
    /// <see cref="OptionsValidationException"/> when one options type fails, and an
    /// <see cref="AggregateException"/> when several do — ours and the resilience package's
    /// can both reject one bad timeout — so tests read the messages, not the wrapper.
    /// </summary>
    public static IReadOnlyList<string> StartupFailures(Action<Dictionary<string, string?>> adjust)
    {
        try
        {
            ValidateLikeStartup(adjust);
            return [];
        }
        catch (OptionsValidationException exception)
        {
            return [.. exception.Failures];
        }
        catch (AggregateException exception)
        {
            return [.. exception.Flatten().InnerExceptions
                .OfType<OptionsValidationException>()
                .SelectMany(inner => inner.Failures)];
        }
    }
}
