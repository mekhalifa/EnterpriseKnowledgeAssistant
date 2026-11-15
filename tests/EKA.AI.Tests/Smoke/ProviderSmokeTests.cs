using EKA.Application.Abstractions.AI;
using EKA.Domain.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit.Abstractions;

namespace EKA.AI.Tests.Smoke;

/// <summary>
/// Step 5's exit criterion, literally: one chat call and one embedding call succeed against
/// the configured provider. Real network, real key, real (tiny) cost — fractions of a cent.
/// </summary>
/// <remarks>
/// Everything is resolved through <c>AddAi</c> and the application's own ports, so a pass
/// proves the configuration, the SDK, the resilience pipeline and the adapters work together
/// — not merely that the provider is up.
/// <para>Run just these with: <c>dotnet test --filter Category=Smoke</c>.</para>
/// </remarks>
[Trait("Category", "Smoke")]
public sealed class ProviderSmokeTests(ITestOutputHelper output)
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAi(SmokeConfiguration.Load());
        return services.BuildServiceProvider();
    }

    [ProviderSmokeFact]
    public async Task One_chat_call_succeeds()
    {
        await using var provider = BuildProvider();

        var result = await provider.GetRequiredService<IChatCompletionService>().CompleteAsync(
        [
            new ChatTurn(MessageRole.System, "You answer with exactly one word."),
            new ChatTurn(MessageRole.User, "Reply with the word: pong"),
        ]);

        result.IsSuccess.ShouldBeTrue(result.ToString());
        output.WriteLine($"model={result.Value.Model} in={result.Value.InputTokens} out={result.Value.OutputTokens}");
        output.WriteLine(result.Value.Content);
        result.Value.Content.ShouldContain("pong", Case.Insensitive);
    }

    [ProviderSmokeFact]
    public async Task One_embedding_call_succeeds_with_the_configured_dimensions()
    {
        await using var provider = BuildProvider();
        var embeddings = provider.GetRequiredService<IEmbeddingService>();

        var result = await embeddings.EmbedAsync(["Enterprise knowledge assistant", "Quarterly leave policy"]);

        result.IsSuccess.ShouldBeTrue(result.ToString());
        output.WriteLine($"model={result.Value.Model} tokens={result.Value.InputTokens} dims={result.Value.Vectors[0].Length}");
        result.Value.Vectors.Count.ShouldBe(2);
        result.Value.Vectors.ShouldAllBe(vector => vector.Length == embeddings.Dimensions);
    }
}
