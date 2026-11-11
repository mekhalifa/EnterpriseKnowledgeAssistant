# Minimal APIs vs MVC Controllers

**Decision: Minimal APIs.** Recorded in [ADR-0003](../adr/0003-minimal-apis.md).

## What each one looks like

**MVC controller** — a class per resource, routing by attributes, dependencies by
constructor:

```csharp
[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(UploadDocumentHandler handler) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Upload([FromForm] UploadRequest request, CancellationToken ct)
    {
        var result = await handler.Handle(request.ToCommand(), ct);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Value }, null)
                                : BadRequest(result.Error);
    }
}
```

**Minimal API** — endpoints are functions, registered into a route group, dependencies
injected per parameter:

```csharp
// EKA.Api/Endpoints/DocumentEndpoints.cs
public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents")
                       .WithTags("Documents")
                       .RequireAuthorization();

        group.MapPost("/", async (UploadRequest request,
                                  UploadDocumentHandler handler,
                                  CancellationToken ct) =>
        {
            var result = await handler.Handle(request.ToCommand(), ct);
            return result.IsSuccess ? Results.Created($"/api/documents/{result.Value}", null)
                                    : Results.Problem(result.Error);
        })
        .DisableAntiforgery()
        .Produces(StatusCodes.Status201Created);
    }
}
```

Both get one line in `Program.cs`: `app.MapDocumentEndpoints();`.

## The comparison

| Dimension | Minimal APIs | Controllers |
|---|---|---|
| Boilerplate | Lower — no class, no base type, no attributes | Higher |
| Startup time & throughput | Faster; skips the MVC filter/model-binding pipeline | Slower, though rarely the bottleneck |
| Dependency injection | Per-parameter, so each endpoint states exactly what it needs | Constructor-level, shared by every action in the class |
| Filters / middleware | `AddEndpointFilter` — per endpoint or per group | Action filters, long-established |
| Validation | Built-in `[Required]`-style validation since .NET 10; FluentValidation via a filter | Automatic model-state validation |
| OpenAPI | First class via `Microsoft.AspNetCore.OpenApi` | First class |
| Model binding | Explicit; complex form/file binding needs more care | Richer conventions (`[FromForm]`, value providers) |
| Convention-based routing | Not supported — every route is explicit | Supported |
| Testability | Handlers are plain functions; most logic sits in `EKA.Application` anyway | Controllers are classes, easy to unit test directly |
| Organization | **You** must impose it — nothing stops a 2000-line `Program.cs` | Imposed by the framework |
| Familiarity | Newer; fewer Stack Overflow answers for exotic cases | 15 years of answers |

## Why Minimal APIs win for EKA

1. **The endpoints are thin.** Our architecture puts orchestration in
   `EKA.Application` handlers. An endpoint's entire job is: bind the request, call the
   handler, map `Result` to an HTTP status. That is a function, not a class — a controller
   would be a wrapper around a wrapper.
2. **Parity arrived.** The historical argument for controllers was missing features —
   validation, filters, antiforgery. As of .NET 9/10 those gaps are closed, so the choice
   is now about style rather than capability.
3. **Per-endpoint dependencies.** A controller with eight actions takes every dependency
   all eight might need. With minimal APIs the upload endpoint asks for the storage
   service and the search endpoint does not — the signature documents the real coupling.
4. **Groups map cleanly to our features.** `MapGroup("/api/documents").RequireAuthorization()`
   applies auth to a whole feature in one place, which matters when a missed
   `[Authorize]` is a security bug.
5. **It is the current default.** `dotnet new webapi` produces minimal APIs; new
   ASP.NET Core guidance and samples lead with them.

## When you should pick controllers instead

Not a trick question — these are real cases:

- You have **dozens of near-identical CRUD endpoints** and want convention-based routing
  or a shared base controller to remove repetition.
- Your team already has deep MVC expertise and a library of custom action filters, model
  binders, or result types.
- You need **server-rendered views** alongside the API (Razor Views share the MVC
  pipeline).
- You rely on OData, or another library that integrates at the MVC layer.

They are not mutually exclusive: ASP.NET Core happily hosts both in one app. Migrating
later is mechanical, endpoint by endpoint — this decision is cheap to reverse, which is
another reason not to agonize over it.

## The discipline Minimal APIs demand

The real failure mode is a `Program.cs` that grows to a thousand lines of inline lambdas.
Our rules:

- **One static class per feature** in `EKA.Api/Endpoints/`, exposing one
  `MapXxxEndpoints()` extension method.
- `Program.cs` only composes: configuration, DI registration, middleware, then one
  `Map…Endpoints()` call per feature.
- **No business logic in a lambda.** If an endpoint body exceeds roughly ten lines, the
  logic belongs in an `EKA.Application` handler.
- Every endpoint declares its response types so the OpenAPI document stays accurate —
  the Angular client's types in Step 13 are generated from it.

## Further reading

- [Minimal APIs overview](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/overview)
- [Route groups](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/route-handlers)
- [Choose between controller-based and minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/apis)
