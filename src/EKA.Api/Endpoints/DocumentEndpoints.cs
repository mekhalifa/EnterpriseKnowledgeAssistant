using EKA.Api.Errors;
using EKA.Api.Filters;
using EKA.Api.Security;
using EKA.Application.Abstractions;
using EKA.Application.Documents.DeleteDocument;
using EKA.Application.Documents.GetDocument;
using EKA.Application.Documents.ListDocuments;
using EKA.Application.Documents.RegisterDocument;
using EKA.Application.Documents.RenameDocument;
using EKA.Application.Documents.ResetDocument;
using EKA.Contracts.Common;
using EKA.Contracts.Documents;

namespace EKA.Api.Endpoints;

/// <summary>
/// The <c>/api/documents</c> route group.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape every endpoint here follows:</b> read the tenant from the authenticated
/// caller, build the command, call the handler, map the <c>Result</c>. Nothing else. The
/// moment a body needs a second decision, that decision belongs in a handler — an endpoint
/// that grows logic is logic that only an HTTP request can test.
/// </para>
/// <para>
/// <b>Authorization is on the group, not the endpoints.</b> One
/// <c>RequireAuthorization(TenantPolicy.Name)</c> covers every route below and every route
/// added later. The alternative — per-endpoint — fails open: the endpoint someone forgets
/// is unprotected and looks exactly like the endpoints that are fine.
/// </para>
/// <para>
/// <b>Why the tenant is read here and passed down.</b> The handlers take a
/// <c>TenantId</c> parameter rather than reading an ambient "current tenant", so they can
/// be tested without a request context and so that reading the tenant from the request body
/// would require visibly deleting this line.
/// </para>
/// </remarks>
internal static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var documents = app.MapGroup("/api/documents")
            .WithTags("Documents")
            .RequireAuthorization(TenantPolicy.Name);

        documents.MapPost("/", RegisterAsync)
            .WithName("RegisterDocument")
            .WithSummary("Registers a document's metadata for ingestion.")
            .WithValidation<RegisterDocumentRequest>()
            .Produces<DocumentResponse>(StatusCodes.Status201Created)
            .Produces<DocumentResponse>(StatusCodes.Status200OK);

        documents.MapGet("/{documentId:guid}", GetAsync)
            .WithName("GetDocument")
            .WithSummary("Reads one document.")
            .Produces<DocumentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        documents.MapGet("/", ListAsync)
            .WithName("ListDocuments")
            .WithSummary("Lists the caller's documents, newest first.")
            .Produces<PagedResponse<DocumentSummaryResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        documents.MapPut("/{documentId:guid}/title", RenameAsync)
            .WithName("RenameDocument")
            .WithSummary("Changes a document's display name.")
            .WithValidation<RenameDocumentRequest>()
            .Produces<DocumentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        documents.MapPost("/{documentId:guid}/reset", ResetAsync)
            .WithName("ResetDocument")
            .WithSummary("Requeues a failed document for ingestion.")
            .Produces<DocumentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        documents.MapDelete("/{documentId:guid}", DeleteAsync)
            .WithName("DeleteDocument")
            .WithSummary("Deletes a document and its chunks.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    /// <remarks>
    /// <b>201 or 200, and why the difference is worth the extra line.</b> Registering bytes
    /// that were already registered is not an error — it is a retry doing its job — so it
    /// must not be a 409. But answering 201 Created for something that was not created
    /// would mislead any client that counts creations. 200 with the existing document is
    /// the truthful answer, and the <c>Location</c> header is correct either way.
    /// </remarks>
    private static async Task<IResult> RegisterAsync(
        RegisterDocumentRequest request,
        RegisterDocumentHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var command = RegisterDocumentCommand.From(request, currentUser.RequireTenantId());

        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return result.Match(outcome => outcome.AlreadyRegistered
            ? Results.Ok(outcome.Document)
            : Results.Created($"/api/documents/{outcome.Document.Id}", outcome.Document));
    }

    private static async Task<IResult> GetAsync(
        Guid documentId,
        GetDocumentHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var query = new GetDocumentQuery(currentUser.RequireTenantId(), documentId);

        var result = await handler.HandleAsync(query, cancellationToken).ConfigureAwait(false);

        return result.Match(Results.Ok);
    }

    private static async Task<IResult> ListAsync(
        ListDocumentsHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken,
        string? status = null,
        int page = 1,
        int pageSize = ListDocumentsHandler.DefaultPageSize)
    {
        var query = new ListDocumentsQuery(currentUser.RequireTenantId(), status, page, pageSize);

        var result = await handler.HandleAsync(query, cancellationToken).ConfigureAwait(false);

        return result.Match(Results.Ok);
    }

    private static async Task<IResult> RenameAsync(
        Guid documentId,
        RenameDocumentRequest request,
        RenameDocumentHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var command = new RenameDocumentCommand(
            currentUser.RequireTenantId(),
            documentId,
            request.Title);

        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return result.Match(Results.Ok);
    }

    /// <remarks>
    /// <c>POST .../reset</c> rather than a <c>PATCH</c> that sets the status back to
    /// Pending. The status is not a field a client may assign — it is the output of a state
    /// machine — and an endpoint shaped like a field assignment invites exactly the request
    /// the domain exists to refuse. Naming the action keeps the API honest about what is
    /// being asked for.
    /// </remarks>
    private static async Task<IResult> ResetAsync(
        Guid documentId,
        ResetDocumentHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var command = new ResetDocumentCommand(currentUser.RequireTenantId(), documentId);

        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return result.Match(Results.Ok);
    }

    private static async Task<IResult> DeleteAsync(
        Guid documentId,
        DeleteDocumentHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var command = new DeleteDocumentCommand(currentUser.RequireTenantId(), documentId);

        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return result.Match(Results.NoContent);
    }
}
