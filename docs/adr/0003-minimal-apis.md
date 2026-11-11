# ADR-0003: Minimal APIs instead of MVC controllers

- **Status:** Accepted
- **Date:** 2026-11-11
- **Deciders:** project owner

## Context

`EKA.Api` needs an HTTP style. Historically controllers were the only serious option for
production APIs because minimal APIs lacked validation, filters, and antiforgery support.
As of .NET 9/10 those gaps are closed, so this is now a style and organization decision
rather than a capability one.

Relevant property of our architecture: **endpoints are thin by design.** Orchestration
lives in `EKA.Application` handlers, so an endpoint's whole job is bind the request, call
the handler, map a `Result` to an HTTP status.

## Decision

Use **Minimal APIs**, organized as one static class per feature in `EKA.Api/Endpoints/`,
each exposing a `MapXxxEndpoints(this IEndpointRouteBuilder)` extension method registered
by a single line in `Program.cs`.

Rules that come with the decision:

- Route groups per feature, with `RequireAuthorization()` applied at the group level.
- No business logic in an endpoint lambda; past roughly ten lines, it moves to a handler.
- Every endpoint declares its response types, because the Angular client's TypeScript
  types in Step 13 are generated from the OpenAPI document.

## Consequences

### Positive

- Substantially less boilerplate: no controller class, base type, or attribute ceremony.
- Faster startup and request throughput — the MVC filter and model-binding pipeline is
  skipped. (Rarely the bottleneck, but free.)
- **Per-endpoint dependency injection**: each endpoint's parameter list states exactly what
  it needs, instead of a controller constructor taking the union of what eight actions
  need.
- Group-level authorization in one place, which matters because a missed `[Authorize]`
  attribute is a security bug.
- It is the current default for `dotnet new webapi` and the direction of ASP.NET Core
  guidance and samples.

### Negative / costs we accept

- **Organization is not enforced by the framework.** Nothing stops `Program.cs` growing to
  a thousand lines of inline lambdas; the conventions above are the mitigation, and they
  must be upheld in review.
- No convention-based routing — every route is written out.
- Complex form and file binding (relevant to the Step 7 upload endpoint) needs more
  explicit handling than `[FromForm]` in a controller.
- Fewer years of accumulated answers for unusual scenarios.

## Alternatives considered

**MVC controllers.** Would win if we had dozens of near-identical CRUD endpoints wanting
convention-based routing or a shared base controller, if we needed server-rendered Razor
views in the same app, if the team had a library of custom action filters and model
binders to reuse, or if we needed a library that integrates at the MVC layer such as OData.
None of these apply.

**FastEndpoints** (third-party, REPR pattern: one class per endpoint). Genuinely good —
it gives minimal-API performance with enforced structure and built-in validation. Rejected
to keep the dependency surface small and to stay on first-party abstractions, which is
better for learning the platform itself. Reasonable to reconsider if endpoint sprawl
becomes a real problem.

**Both in one app.** ASP.NET Core supports it, and migration either direction is
mechanical and endpoint-by-endpoint. Noted mainly to record that **this decision is cheap
to reverse**, which is itself a reason not to over-deliberate.

## Revisit when

- `Program.cs` or the endpoints folder becomes hard to navigate despite the conventions.
- A requirement appears for Razor views, OData, or another MVC-layer integration.
- File-upload binding in Step 7 proves materially harder than the controller equivalent.
