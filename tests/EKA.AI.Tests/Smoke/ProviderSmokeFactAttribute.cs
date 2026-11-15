using Microsoft.Extensions.Configuration;

namespace EKA.AI.Tests.Smoke;

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself unless a real provider key is configured.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a skip and not a failure,</b> when the integration tests deliberately fail without
/// Docker: Docker is free and every contributor is expected to have it, whereas a provider
/// key costs money and is personal. A suite that fails for everyone without a paid account is
/// a suite people learn to ignore. The skip reason is printed in the test output, so it is
/// visible rather than silent.
/// </para>
/// <para>
/// <b>Where the key is read from</b> — the same places <c>EKA.Api</c> reads it, in the same
/// order: <c>src/EKA.Api/appsettings.json</c> for the non-secret settings, then that
/// project's User Secrets, then environment variables. One <c>dotnet user-secrets set</c>
/// configures both the app and this test.
/// </para>
/// <para>
/// xUnit v2 has no runtime <c>Assert.Skip</c>; setting <see cref="FactAttribute.Skip"/> in the
/// attribute's constructor is the supported way to decide at discovery time.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ProviderSmokeFactAttribute : FactAttribute
{
    public ProviderSmokeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(SmokeConfiguration.Load()["Ai:ApiKey"]))
        {
            Skip = "No AI provider key configured. Run: dotnet user-secrets --project src/EKA.Api "
                + "set \"Ai:ApiKey\" \"<key>\"  (or set Ai__ApiKey). This test makes real, billed calls.";
        }
    }
}

internal static class SmokeConfiguration
{
    /// <summary>Must equal &lt;UserSecretsId&gt; in src/EKA.Api/EKA.Api.csproj.</summary>
    private const string ApiUserSecretsId = "eka-api-6f3b2a10-5d41-4c8e-9a77-21b0c9e4d5f3";

    public static IConfiguration Load() => new ConfigurationBuilder()
        .AddJsonFile(ApiAppSettingsPath(), optional: false)
        .AddUserSecrets(ApiUserSecretsId)
        .AddEnvironmentVariables()
        .Build();

    /// <summary>
    /// Reads the API's real appsettings.json rather than a copy, so the smoke test exercises
    /// the configuration that ships, not one that drifted from it.
    /// </summary>
    private static string ApiAppSettingsPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EnterpriseKnowledgeAssistant.slnx")))
            {
                return Path.Combine(directory.FullName, "src", "EKA.Api", "appsettings.json");
            }
        }

        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
