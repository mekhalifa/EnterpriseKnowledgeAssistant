using System.Net;
using EKA.AI.Tests.Support;
using EKA.Application.Abstractions.AI;
using EKA.Application.Common;
using EKA.Domain.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EKA.AI.Tests;

/// <summary>
/// The resilience pipeline, exercised end to end through the real SDK with a scripted
/// network. No API key is needed and nothing leaves the process.
/// </summary>
public sealed class ResilienceTests
{
    private static readonly ChatTurn[] Ping = [new(MessageRole.User, "ping")];

    private static async Task<Result<ChatCompletion>> Complete(
        ScriptedHandler handler,
        Action<Dictionary<string, string?>>? adjust = null,
        CancellationToken cancellationToken = default)
    {
        await using var provider = AiTestHost.Build(handler, adjust);
        return await provider.GetRequiredService<IChatCompletionService>()
            .CompleteAsync(Ping, cancellationToken);
    }

    [Fact]
    public async Task A_transient_503_is_retried_and_the_call_succeeds()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.ServiceUnavailable)
            .Then(HttpStatusCode.OK, ScriptedHandler.ChatJson("pong"));

        var result = await Complete(handler);

        result.IsSuccess.ShouldBeTrue(result.ToString());
        result.Value.Content.ShouldBe("pong");
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_429_is_retried()
    {
        var handler = new ScriptedHandler()
            .Then(HttpStatusCode.TooManyRequests)
            .Then(HttpStatusCode.OK, ScriptedHandler.ChatJson("pong"));

        var result = await Complete(handler);

        result.IsSuccess.ShouldBeTrue(result.ToString());
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_sustained_outage_makes_exactly_one_plus_max_retries_requests()
    {
        // The number is the assertion. 4 proves the HTTP pipeline retried three times and the
        // SDK's own retry policy is really off: with both active this would be 16, each a
        // request the provider may bill.
        var handler = new ScriptedHandler().Then(HttpStatusCode.ServiceUnavailable);

        var result = await Complete(handler);

        handler.Calls.ShouldBe(4);
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(AiErrors.Unavailable);
        result.Error.Type.ShouldBe(ErrorType.Provider);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "ai.authentication_failed")]
    [InlineData(HttpStatusCode.Forbidden, "ai.authentication_failed")]
    [InlineData(HttpStatusCode.BadRequest, "ai.rejected")]
    [InlineData(HttpStatusCode.NotFound, "ai.rejected")]
    // Out of credit: not retried now (it will not heal within a request), and not "rejected",
    // which indexing treats as permanent (Step 11).
    [InlineData(HttpStatusCode.PaymentRequired, "ai.quota_exceeded")]
    public async Task A_client_error_is_not_retried(HttpStatusCode status, string expectedCode)
    {
        // A wrong key or an unknown model fails identically on every attempt. Retrying it
        // only adds latency — and, for a 401, hammers the provider with a bad credential.
        var handler = new ScriptedHandler().Then(status);

        var result = await Complete(handler);

        handler.Calls.ShouldBe(1);
        result.Error.Code.ShouldBe(expectedCode);
        result.Error.Type.ShouldBe(ErrorType.Provider);
    }

    [Fact]
    public async Task A_hung_attempt_is_cut_off_by_the_attempt_timeout_and_retried()
    {
        var handler = new ScriptedHandler()
            .ThenHang()
            .Then(HttpStatusCode.OK, ScriptedHandler.ChatJson("pong"));

        var result = await Complete(handler);

        result.IsSuccess.ShouldBeTrue(result.ToString());
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_provider_that_never_answers_returns_a_timeout_error_not_an_exception()
    {
        var handler = new ScriptedHandler().ThenHang();

        var result = await Complete(handler, settings => settings["Ai:Resilience:MaxRetryAttempts"] = "1");

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(AiErrors.Timeout);
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_becoming_a_provider_error()
    {
        // The caller left. Answering with a 502 "provider timed out" would be false, and would
        // skew the provider-failure metrics with every closed browser tab.
        var handler = new ScriptedHandler().ThenHang();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(
            () => Complete(
                handler,
                settings => settings["Ai:Resilience:AttemptTimeout"] = "00:00:03",
                cancellation.Token));
    }

    [Fact]
    public async Task Requests_go_to_the_configured_endpoint()
    {
        // The key is sent wherever this points. A wrong base address is not a cosmetic bug.
        var handler = new ScriptedHandler().Then(HttpStatusCode.OK, ScriptedHandler.ChatJson("pong"));

        await Complete(handler);

        handler.RequestUris.Single().ToString().ShouldBe("https://ai.example.test/v1/chat/completions");
    }
}
