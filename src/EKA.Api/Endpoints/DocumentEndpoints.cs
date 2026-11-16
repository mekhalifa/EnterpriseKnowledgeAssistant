using FluentValidation;
using EKA.Api.Errors;
using EKA.Api.Filters;
using EKA.Api.Security;
using EKA.Application.Abstractions;
using EKA.Application.Documents.DeleteDocument;
using EKA.Application.Documents.GetDocument;
using EKA.Application.Documents.ListDocuments;
using EKA.Application.Documents.RenameDocument;
using EKA.Application.Documents.ResetDocument;
using EKA.Application.Documents.SetDocumentAccess;
using EKA.Application.Documents.UploadDocument;
using EKA.Application.Documents.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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

        var maxUploadBytes = app.ServiceProvider.GetRequiredService<IOptions<IngestionOptions>>().Value.MaxUploadBytes;

        documents.MapPost("/", UploadAsync)
            .WithName("UploadDocument")
            .WithSummary("Uploads a file for ingestion. The same bytes uploaded twice return the first document.")
            .Accepts<UploadDocumentMetadata>("multipart/form-data")
            .ProducesValidationProblem()
            // The server cuts the body off once it passes this size, while it is still
            // streaming in, answering 413 before the file is ever buffered whole. The
            // allowance above MaxUploadBytes covers multipart boundaries and form fields. The
            // file's own size is enforced exactly by the use case (document.file_too_large).
            .WithMetadata(new RequestSizeLimitAttribute(maxUploadBytes + MultipartOverheadBytes))
            // Antiforgery protects cookie-authenticated browser forms. This API authenticates
            // every request explicitly, so a cross-site page cannot submit as the user, and a
            // token would protect nothing. Revisit if Step 13's client uses cookies.
            .DisableAntiforgery()
            .Produces<DocumentResponse>(StatusCodes.Status201Created)
            .Produces<DocumentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

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

        documents.MapPut("/{documentId:guid}/access", SetAccessAsync)
            .WithName("SetDocumentAccess")
            .WithSummary("Replaces who may read a document: [\"tenant\"], or user:<subject> and group:<id> keys.")
            .WithValidation<SetDocumentAccessRequest>()
            .Produces<DocumentResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        documents.MapDelete("/{documentId:guid}", DeleteAsync)
            .WithName("DeleteDocument")
            .WithSummary("Deletes a document and its chunks.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>Room for multipart framing and the small form fields around the file.</summary>
    private const long MultipartOverheadBytes = 64 * 1024;

    /// <remarks>
    /// <para>
    /// <b>201 or 200, and why the difference is worth the extra line.</b> Uploading bytes
    /// that are already registered is not an error. It is a retry doing its job, so it must
    /// not be a 409. But answering 201 Created for something that was not created would
    /// mislead any client that counts creations. 200 with the existing document is the
    /// truthful answer.
    /// </para>
    /// <para>
    /// <b>Why this endpoint validates inline instead of using <c>WithValidation</c>.</b> The
    /// filter finds its request among the endpoint's arguments, and it treats a missing one
    /// as "no body" (the right call for JSON). A <c>[FromForm]</c> record binds to null when
    /// the optional fields are simply absent, so the filter rejected every upload that sent
    /// a file and nothing else (step-07 §5.3). The two fields are therefore bound as plain
    /// optional parameters, and the same validator runs here. The rules stay in one place
    /// (<c>UploadDocumentMetadataValidator</c>); only who calls them differs.
    /// </para>
    /// </remarks>
    private static async Task<IResult> UploadAsync(
        IFormFile file,
        [FromForm] string? title,
        [FromForm] int? authority,
        IValidator<UploadDocumentMetadata> validator,
        UploadDocumentHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var metadata = new UploadDocumentMetadata(title, authority);

        var validation = await validator.ValidateAsync(metadata, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return TypedResults.ValidationProblem(validation.ToDictionary());
        }

        // OpenReadStream over ASP.NET Core's buffered form file is seekable, which the handler
        // requires: it reads the header to detect the type, then rewinds.
        await using var content = file.OpenReadStream();

        var command = new UploadDocumentCommand(
            currentUser.RequireAccessScope(),
            file.FileName,
            content,
            metadata.Title,
            metadata.Authority);

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
        var query = new GetDocumentQuery(currentUser.RequireAccessScope(), documentId);

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
        var query = new ListDocumentsQuery(currentUser.RequireAccessScope(), status, page, pageSize);

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
            currentUser.RequireAccessScope(),
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
        var command = new ResetDocumentCommand(currentUser.RequireAccessScope(), documentId);

        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return result.Match(Results.Ok);
    }

    private static async Task<IResult> SetAccessAsync(
        Guid documentId,
        SetDocumentAccessRequest request,
        SetDocumentAccessHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var command = new SetDocumentAccessCommand(currentUser.RequireAccessScope(), documentId, request.Principals);

        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return result.Match(Results.Ok);
    }

    private static async Task<IResult> DeleteAsync(
        Guid documentId,
        DeleteDocumentHandler handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var command = new DeleteDocumentCommand(currentUser.RequireAccessScope(), documentId);

        var result = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return result.Match(Results.NoContent);
    }
}
