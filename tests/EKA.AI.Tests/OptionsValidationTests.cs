using EKA.AI.Tests.Support;
using Shouldly;

namespace EKA.AI.Tests;

/// <summary>
/// A misconfigured provider must stop the process at boot. Each test runs the same startup
/// validation the host runs, and names the configuration key in the failure it expects —
/// because the message is what the operator reads at 2 a.m.
/// </summary>
public sealed class OptionsValidationTests
{
    [Fact]
    public void The_valid_test_configuration_passes()
    {
        AiTestHost.StartupFailures(_ => { }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Ai:ApiKey", "", "Ai:ApiKey")]
    [InlineData("Ai:ChatModel", "", "Ai:ChatModel")]
    [InlineData("Ai:EmbeddingModel", " ", "Ai:EmbeddingModel")]
    [InlineData("Ai:EmbeddingDimensions", "0", "Ai:EmbeddingDimensions")]
    [InlineData("Ai:EmbeddingDimensions", "100000", "Ai:EmbeddingDimensions")]
    [InlineData("Ai:Kind", "AzureOpenAI", "not supported")]
    [InlineData("Ai:Endpoint", "http://openrouter.ai/api/v1", "https")]
    [InlineData("Ai:Resilience:TotalTimeout", "00:00:00.100", "Ai:Resilience:TotalTimeout")]
    [InlineData("Ai:Resilience:MaxRetryAttempts", "50", "Ai:Resilience:MaxRetryAttempts")]
    public void A_bad_setting_stops_startup_and_names_the_key(string key, string value, string expectedInMessage)
    {
        var failures = AiTestHost.StartupFailures(settings => settings[key] = value);

        failures.ShouldContain(message => message.Contains(expectedInMessage), string.Join(" | ", failures));
    }

    [Fact]
    public void An_OpenAI_compatible_provider_without_an_endpoint_stops_startup()
    {
        // Without an endpoint the SDK defaults to api.openai.com and sends an OpenRouter key
        // to OpenAI.
        var failures = AiTestHost.StartupFailures(settings => settings.Remove("Ai:Endpoint"));

        failures.ShouldContain(message => message.Contains("Ai:Endpoint"));
    }

    [Fact]
    public void A_plain_http_endpoint_is_allowed_on_loopback_for_local_models()
    {
        AiTestHost.StartupFailures(settings => settings["Ai:Endpoint"] = "http://localhost:11434/v1")
            .ShouldBeEmpty();
    }

    [Fact]
    public void Every_failure_is_reported_once_and_all_at_once()
    {
        // Fix-one-restart-find-the-next is a miserable way to configure a service — and the
        // same failure reported twice (step-05 §5.1) is nearly as bad.
        var failures = AiTestHost.StartupFailures(settings =>
        {
            settings["Ai:ApiKey"] = "";
            settings["Ai:EmbeddingDimensions"] = "0";
        });

        failures.Count.ShouldBe(2, string.Join(" | ", failures));
    }
}
