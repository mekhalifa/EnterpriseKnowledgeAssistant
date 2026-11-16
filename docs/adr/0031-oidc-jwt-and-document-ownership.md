# ADR-0031: OIDC access tokens from any issuer, Keycloak locally; document owners and tenant administrators

- **Status:** Accepted
- **Date:** 2026-11-16
- **Deciders:** project owner
- **Supersedes:** [ADR-0014](0014-development-authentication.md)

## Context

Step 12's exit criterion: *security tests prove unauthorized content cannot be retrieved.*
Until now identity came from request headers, through a development scheme that
authenticated nobody (ADR-0014). Everything downstream already read only standard claims
(`sub`, `tid`, `groups`). Any reader of a document could also delete it or re-share it.

## Decision

**Authentication**

- **JWT bearer access tokens from any OpenID Connect issuer.** Configuration names the
  `Authority` and the `Audience`. Signing keys and issuer come from the authority's published
  metadata. Validated: signature (asymmetric, from JWKS), issuer, audience, lifetime (one
  minute of clock skew, not five), and the presence of an expiry.
- **Authorization policy `TenantScoped`:** an authenticated user, a `tid` claim, and the
  `eka.access` scope (`scope` for Keycloak, `scp` for Entra ID).
- **Claim names as issued** (`MapInboundClaims = false`).
- **The development header scheme is deleted**, as ADR-0014 promised.
- **Startup refuses to run** without an Authority and an Audience, or with
  `RequireHttpsMetadata=false` outside Development.
- **Locally: Keycloak 26.6.4 in compose**, with a checked-in realm: test users, groups,
  `eka.admin`, and claim mappers for `tid`, `groups`, `roles` and audience. The `tid`
  attribute is editable by administrators only. A user who could edit their own tenant id
  could move tenants.
- **Production: Entra ID** (or a managed Keycloak) by configuration.
- **Tests sign their own tokens** with a key the API is told to trust through
  `JwtBearerOptions.Configuration`. No production code knows about it.

**Management** (from the owner's choice of "owner + tenant admin")

- A document records its **owner** (the uploader's subject). Existing documents are
  backfilled from `CreatedBy`, and documents created by the system stay unowned.
- **Manage** (rename, reset, delete, change access) = owner, or a caller with role `eka.admin`
  in the same tenant.
- **Visible** (metadata, list) = may read, or may manage.
- **Content** (search, answers) = the access list only. **Administrators manage. They do not
  read** restricted content through the assistant.
- A reader who may not manage gets **403**: the document's existence is no secret to them.
  A caller who cannot see the document gets **404**, as before.

## Consequences

### Positive

- A forged, expired, foreign-audience, foreign-issuer, unsigned or tampered token reaches no
  handler, each proven by a test against the production validation path.
- Changing provider is configuration. All 121 existing integration tests passed on the token
  path unchanged.
- An administrator can rescue a document whose access list locks everyone out, without
  thereby gaining read access to it.

### Negative / costs we accept

- **Local development needs Keycloak running** (about 70 s to start the first time) and a
  token for every request (`deploy/keycloak/get-token.ps1`). The password grant is enabled
  in the local realm for this, and must be off in any deployed realm.
- **Group overage (Entra ID, more than 200 groups)** gives the caller no groups: they see less,
  never more. Resolving it needs a Graph call and a permission the API does not have.
- **Administrators see restricted documents' titles,** in order to manage them. A title can itself
  be sensitive.
- **Owner by subject id:** if a person leaves, their documents need an administrator.
- **Tokens are not revoked before expiry.** A disabled user keeps access for up to five
  minutes (the realm's token lifetime).

## Alternatives considered

- **Entra ID directly.** The most realistic option. It wins when a tenant and app registration
  are available. It needs setup only the owner can do, and an internet sign-in for every local
  run.
- **ASP.NET Core Identity.** No dependency. It wins for a standalone product with no corporate
  directory. It means no single sign-on and no enterprise groups, and the application owns
  passwords.
- **Admins only manage.** The simplest to audit. It wins for a curated knowledge base. It means
  uploaders cannot fix their own typos.
- **Admins read everything.** Convenient. It violates least privilege: the administrator of a
  document system is not entitled to the HR folder.

## Revisit when

- The deployment target is chosen: configure Entra ID, and disable the password grant.
- Groups overage is observed in practice: add the Graph call, with its permission.
- Token revocation matters (fired employees): shorter lifetimes, or introspection.
