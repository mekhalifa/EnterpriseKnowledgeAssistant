# ADR-0014: A development authentication scheme until an identity provider exists

- **Status:** Accepted
- **Date:** 2026-11-12
- **Deciders:** project owner

## Context

Step 3 built the security property this system turns on: every repository method takes a
`TenantId` and applies it inside the query, so a caller cannot forget a filter it was never
able to write (ADR-0009). Step 4 put endpoints in front of those repositories and
immediately ran into the gap — **the endpoints have no tenant to pass.** `ICurrentUser`
reads claims from `HttpContext`, no identity provider is configured until Step 12, and so
it returns null for every request.

Three ways out, and two of them are bad:

- **Accept the tenant from the request.** A path parameter, a header read directly, a field
  in the body. This reopens precisely the hole ADR-0009 closed, and it does so in a way that
  is hard to remove later because clients start depending on it.
- **Ship no endpoints until Step 12.** Steps 5 through 11 all need a working API to be
  testable, and deferring would mean eight steps of code with nothing exercising it.
- **Stand in for the identity provider**, in a way that cannot escape development.

A fourth option deserves naming because it is the tempting one: a single hard-coded
development tenant constant. It is the smallest change, and it makes the single most
valuable test in the suite impossible to write — two callers from different tenants
proving neither can see the other's documents.

## Decision

Register a **development-only authentication scheme** that builds a `ClaimsPrincipal` from
request headers.

- Scheme name `DevelopmentHeader`; headers `X-EKA-Tenant` and `X-EKA-Subject`.
- It issues the same claims a real OIDC provider would: `sub`, `ClaimTypes.NameIdentifier`
  and `tid`. Nothing downstream — `HttpContextCurrentUser`, the repositories, the audit
  interceptor, any endpoint — knows this scheme exists.
- Absent headers fall back to configured defaults so the OpenAPI page works unconfigured.
  A **present but malformed** tenant header is rejected with 401, never defaulted: silently
  substituting the default tenant would make a typo in a cross-tenant test look like a pass.
- Endpoints are protected by a `TenantScoped` policy applied to the **route group**, which
  requires an authenticated user *and* the `tid` claim. A token that authenticates a person
  but carries no tenant has nothing to filter by, and the only alternatives are deny or
  invent.

**Three independent gates keep it inside development:**

1. `Authentication:Development:Enabled` defaults to `false`.
2. `Program.cs` registers the scheme only when `IHostEnvironment.IsDevelopment()`.
3. Startup **throws** if `Enabled` is true in any other environment, so a configuration file
   copied to staging stops the deployment instead of accepting any tenant anyone types.

When the scheme is not registered, a `NoIdentityProvider` scheme takes its place and
authenticates nobody, so protected endpoints answer `401`. Without it, ASP.NET Core has no
default scheme and `RequireAuthenticatedUser()` throws — turning "I sent no credentials"
into a `500` that reads as a server fault.

## Consequences

### Positive

- The tenant reaches the repositories from an authenticated principal from the first
  endpoint, never from client-controlled request content.
- `RequireAuthorization` runs in CI from Step 4 rather than being exercised for the first
  time in Step 12, when it is load-bearing.
- Cross-tenant isolation is testable today: two `HttpClient`s with different tenant headers
  against one server. Those are the highest-value tests in the suite.
- Step 12 replaces one `AddAuthentication` block with JWT bearer validation. No endpoint,
  handler, repository or test fixture changes, because none of them reads anything but the
  principal.
- The unconfigured state answers `401`, which is the truth.

### Negative / costs we accept

- **This authenticates nobody.** Anyone who can reach the port can claim any tenant. That is
  acceptable only because of the three gates, and it is why the handler is `internal` and
  says so in its own name.
- Roughly 150 lines of code and configuration that exist to be deleted.
- A third gate that nothing currently tests across environments — the startup throw is
  asserted by reading the code, not by a test, because the test would have to boot the app
  in Staging with the flag on.
- `appsettings.Development.json` ships with `Enabled: true`, so a developer who renames
  their environment inherits a surprise. The startup throw converts that surprise into a
  clear error rather than a silent acceptance.

## Alternatives considered

**A hard-coded development tenant in `HttpContextCurrentUser`.** Three lines instead of 150.
Wins if cross-tenant behaviour were being deferred wholesale to Step 12. Loses because
isolation is the risk this project tracks as most severe, and this option makes it
untestable for eight steps — the gap between "we wrote a tenant filter" and "we proved it
holds across the HTTP boundary".

**Wire the real identity provider now.** Honest, and it would delete this ADR. Wins if an
Entra ID tenant or a Keycloak instance were already available and the provider choice were
settled. Loses today: the provider decision is open (`docs/PROGRESS.md`), it brings issuer
and audience validation, token lifetimes, key rotation and a test strategy for signed
tokens, and doing it badly now is worse than doing it deliberately in Step 12.

**Anonymous endpoints taking the tenant as a route parameter.** Wins for a genuinely
single-tenant internal tool. Loses here completely: it is the data-breach shape this
architecture is built to prevent, and it is the hardest to walk back because it becomes part
of the published URL.

**Test-only authentication, with the real app left unauthenticated.** A handler registered
by `WebApplicationFactory` rather than by `Program.cs`. Wins by keeping the stand-in out of
the production assembly entirely — genuinely attractive. Loses because then nobody can run
the application locally and click a request, and because the authentication pipeline under
test stops being the one that ships.

## Revisit when

Step 12 — this ADR is superseded the moment a real identity provider is configured, and the
scheme, its options, its handler and `NoIdentityProviderHandler` are deleted rather than
disabled.
