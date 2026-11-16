using System.Net;
using EKA.AI.Tests.Support;
using EKA.Application.Abstractions.AI;
using EKA.Domain.Common;
using EKA.Domain.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EKA.AI.Tests;

/// <summary>
/// The translation between application vocabulary and the wire, in both directions.
/// </summary>
public sealed class AdapterTests
{
    [Fact]
    public async Task Chat_roles_reach_the_wire_and_usage_comes_back_as_token_counts()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, ScriptedHandler.ChatJson("an answer"));
        await using var provider = AiTestHost.Build(handler);

        var result = await provider.GetRequiredService<IChatCompletionService>().CompleteAsync(
        [
            new ChatTurn(MessageRole.System, "be brief"),
            new ChatTurn(MessageRole.User, "question"),
        ]);

        result.IsSuccess.ShouldBeTrue(result.ToString());
        result.Value.Content.ShouldBe("an answer");
        result.Value.Model.ShouldBe("test-chat");
        result.Value.InputTokens.ShouldBe(new TokenCount(12));
        result.Value.OutputTokens.ShouldBe(new TokenCount(3));

        var body = handler.RequestBodies.Single();
        body.ShouldContain("\"role\":\"system\"");
        body.ShouldContain("\"model\":\"test-chat\"");
    }

    [Fact]
    public async Task A_configured_temperature_reaches_the_wire_and_an_absent_one_is_not_sent()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.OK, ScriptedHandler.ChatJson("a"))
            .Then(HttpStatusCode.OK, ScriptedHandler.ChatJson("b"));

        await using (var withZero = AiTestHost.Build(handler, settings => settings["Ai:Temperature"] = "0"))
        {
            await withZero.GetRequiredService<IChatCompletionService>().CompleteAsync([new ChatTurn(MessageRole.User, "q")]);
        }

        await using (var unset = AiTestHost.Build(handler))
        {
            await unset.GetRequiredService<IChatCompletionService>().CompleteAsync([new ChatTurn(MessageRole.User, "q")]);
        }

        handler.RequestBodies[0].ShouldContain("\"temperature\":0");
        handler.RequestBodies[1].ShouldNotContain("temperature");
    }

    [Fact]
    public async Task A_configured_output_cap_reaches_the_wire()
    {
        // Without one, every request reserves the model's maximum output, and a provider that
        // pre-authorizes credit refuses requests it could easily afford (step-11).
        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, ScriptedHandler.ChatJson("a"));
        await using var provider = AiTestHost.Build(handler, settings => settings["Ai:MaxOutputTokens"] = "1000");

        await provider.GetRequiredService<IChatCompletionService>().CompleteAsync([new ChatTurn(MessageRole.User, "q")]);

        handler.RequestBodies.Single().ShouldMatch("\"max_(completion_)?tokens\":1000");
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("2.5")]
    public void A_temperature_the_provider_would_reject_stops_startup(string temperature)
    {
        AiTestHost.StartupFailures(settings => settings["Ai:Temperature"] = temperature)
            .ShouldContain(failure => failure.Contains("Temperature"));
    }

    [Fact]
    public async Task A_chat_reply_with_no_text_is_a_provider_error_not_a_blank_answer()
    {
        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, ScriptedHandler.ChatJson(""));
        await using var provider = AiTestHost.Build(handler);

        var result = await provider.GetRequiredService<IChatCompletionService>()
            .CompleteAsync([new ChatTurn(MessageRole.User, "question")]);

        result.Error.ShouldBe(AiErrors.EmptyResponse);
    }

    [Fact]
    public async Task Embeddings_request_the_configured_dimensions_explicitly()
    {
        var handler = new ScriptedHandler()
            .ThenFromRequest(body => ScriptedHandler.EmbeddingJson(body, 1, AiTestHost.Dimensions));
        await using var provider = AiTestHost.Build(handler);

        await provider.GetRequiredService<IEmbeddingService>().EmbedAsync(["hello"]);

        handler.RequestBodies.Single().ShouldContain($"\"dimensions\":{AiTestHost.Dimensions}");
    }

    [Fact]
    public async Task Embeddings_come_back_index_aligned_with_their_inputs()
    {
        var handler = new ScriptedHandler()
            .ThenFromRequest(body => ScriptedHandler.EmbeddingJson(body, 2, AiTestHost.Dimensions));
        await using var provider = AiTestHost.Build(handler);

        var result = await provider.GetRequiredService<IEmbeddingService>().EmbedAsync(["first", "second"]);

        result.IsSuccess.ShouldBeTrue(result.ToString());
        result.Value.Vectors.Count.ShouldBe(2);
        result.Value.Vectors[0].Span[0].ShouldBe(0f);
        result.Value.Vectors[1].Span[0].ShouldBe(1f);
        result.Value.InputTokens.ShouldBe(new TokenCount(7));
    }

    [Fact]
    public async Task A_vector_of_the_wrong_length_is_rejected_before_it_can_be_stored()
    {
        // The model ignored the dimensions parameter. Startup validation cannot see this; only
        // checking what actually came back can.
        var handler = new ScriptedHandler()
            .ThenFromRequest(body => ScriptedHandler.EmbeddingJson(body, 1, AiTestHost.Dimensions + 1));
        await using var provider = AiTestHost.Build(handler);

        var result = await provider.GetRequiredService<IEmbeddingService>().EmbedAsync(["hello"]);

        result.Error.ShouldBe(AiErrors.DimensionMismatch);
    }

    [Fact]
    public async Task Fewer_vectors_than_inputs_is_rejected_because_alignment_is_lost()
    {
        var handler = new ScriptedHandler()
            .ThenFromRequest(body => ScriptedHandler.EmbeddingJson(body, 1, AiTestHost.Dimensions));
        await using var provider = AiTestHost.Build(handler);

        var result = await provider.GetRequiredService<IEmbeddingService>().EmbedAsync(["a", "b"]);

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task An_empty_batch_is_a_programming_error()
    {
        await using var provider = AiTestHost.Build(new ScriptedHandler());

        await Should.ThrowAsync<ArgumentException>(
            () => provider.GetRequiredService<IEmbeddingService>().EmbedAsync([]));
    }
}
