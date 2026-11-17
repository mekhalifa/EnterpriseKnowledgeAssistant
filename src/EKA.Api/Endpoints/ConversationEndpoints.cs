using EKA.Api.Errors;
using EKA.Api.Filters;
using EKA.Api.Security;
using EKA.Api.Streaming;
using EKA.Application.Abstractions;
using EKA.Application.Conversations.AskQuestion;
using EKA.Application.Conversations.GetConversation;
using EKA.Application.Conversations.ListConversations;
using EKA.Application.Conversations.StartConversation;
using EKA.Contracts.Conversations;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace EKA.Api.Endpoints;

/// <summary>
/// The chat surface: start a conversation, ask in it, read it back.
/// </summary>
/// <remarks>
/// <para>
/// Every route is scoped to the calling <i>user</i>, not only the tenant: a colleague's
/// conversation is a 404, exactly like another tenant's document. The scoping happens in
/// the repository query, never here.
/// </para>
/// <para>
/// <b>Asking is <c>POST …/messages</c>, and it answers 200, not 201.</b> One request creates
/// two resources (the question and the answer), and neither is addressable on its own;
/// both are read back through the conversation. A 201 would owe the client a
/// <c>Location</c> for something that has none.
/// </para>
/// </remarks>
internal static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var conversations = app.MapGroup("/api/conversations")
            .WithTags("Conversations")
            .RequireAuthorization(TenantPolicy.Name);

        conversations.MapPost("/", StartAsync)
            .WithName("StartConversation")
            .WithSummary("Opens an empty conversation for the caller.")
            .Produces<ConversationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        conversations.MapGet("/", ListAsync)
            .WithName("ListConversations")
            .WithSummary("Lists the caller's active conversations, most recently used first.")
            .Produces<IReadOnlyList<ConversationSummaryResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        conversations.MapGet("/{conversationId:guid}", GetAsync)
            .WithName("GetConversation")
            .WithSummary("Reads one conversation with its messages in order.")
            .Produces<ConversationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        conversations.MapPost("/{conversationId:guid}/messages", AskAsync)
            .WithName("AskQuestion")
            .RequireRateLimiting(RateLimitingOptions.AskPolicy)
            .WithSummary("Asks a question in a conversation and returns the persisted exchange.")
            .WithValidation<AskQuestionRequest>()
            .Produces<AskQuestionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        // Step 13: the same use case as server-sent events, so the client can show the answer as
        // it is written. Same rate limit, same validation, same handler (ADR-0033).
        conversations.MapPost("/{conversationId:guid}/messages/stream", AskStreamingAsync)
            .WithName("AskQuestionStreaming")
            .RequireRateLimiting(RateLimitingOptions.AskPolicy)
            .WithSummary("Asks a question and streams the answer as server-sent events: "
                + "'delta' events while the model writes, then one 'completed' (the persisted exchange) "
                + "or 'error' (a problem document; nothing persisted).")
            .WithValidation<AskQuestionRequest>()
            .Produces(StatusCodes.Status200OK, contentType: ServerSentEventWriter.ContentType)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        return app;
    }

    private static async Task<IResult> StartAsync(
        StartConversationHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var command = new StartConversationCommand(currentUser.RequireCaller());

        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return result.Match(conversation =>
            Results.Created($"/api/conversations/{conversation.Id}", conversation));
    }

    private static async Task<IResult> ListAsync(
        ListConversationsHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken,
        int limit = ListConversationsHandler.DefaultLimit)
    {
        var query = new ListConversationsQuery(currentUser.RequireCaller(), limit);

        var result = await handler.HandleAsync(query, cancellationToken).ConfigureAwait(false);

        return result.Match(Results.Ok);
    }

    private static async Task<IResult> GetAsync(
        Guid conversationId,
        GetConversationHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var query = new GetConversationQuery(currentUser.RequireCaller(), conversationId);

        var result = await handler.HandleAsync(query, cancellationToken).ConfigureAwait(false);

        return result.Match(Results.Ok);
    }

    private static async Task<IResult> AskAsync(
        Guid conversationId,
        AskQuestionRequest request,
        AskQuestionHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var command = new AskQuestionCommand(currentUser.RequireCaller(), conversationId, request.Question);

        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return result.Match(Results.Ok);
    }

    /// <remarks>
    /// <para>
    /// The response starts with the first delta (see <see cref="ServerSentEventWriter"/>). Until
    /// then every failure is the ordinary problem response with its own status, exactly as the
    /// non-streaming endpoint would answer. After it, a failure is an <c>error</c> event and the
    /// client discards what it showed; the handler has persisted nothing.
    /// </para>
    /// <para>
    /// A no-evidence refusal never calls the model, so it produces no deltas: the stream starts and
    /// ends with its single <c>completed</c> event.
    /// </para>
    /// <para>
    /// If the browser disconnects, the request token is cancelled, the provider call is abandoned
    /// and nothing is persisted: the same outcome as a failed turn.
    /// </para>
    /// </remarks>
    private static async Task<IResult> AskStreamingAsync(
        Guid conversationId,
        AskQuestionRequest request,
        AskQuestionHandler handler,
        ICurrentUser currentUser,
        HttpContext httpContext,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        var command = new AskQuestionCommand(currentUser.RequireCaller(), conversationId, request.Question);
        var stream = new ServerSentEventWriter(httpContext.Response, jsonOptions.Value.SerializerOptions);

        var result = await handler
            .StreamAsync(
                command,
                (text, token) => stream.WriteAsync(AnswerStreamEvents.Delta, new AnswerDeltaEvent(text), token),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            if (!stream.HasStarted)
            {
                return result.Error.ToProblem();
            }

            await stream.WriteAsync(AnswerStreamEvents.Error, result.Error.ToProblemDetails(httpContext), cancellationToken)
                .ConfigureAwait(false);
            return Results.Empty;
        }

        await stream.WriteAsync(AnswerStreamEvents.Completed, result.Value, cancellationToken).ConfigureAwait(false);
        return Results.Empty;
    }
}
