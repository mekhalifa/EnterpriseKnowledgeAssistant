# ADR-0017: Just-in-time user provisioning, unique per tenant

- **Status:** Accepted
- **Date:** 2026-11-15
- **Deciders:** project owner

## Context

A `Conversation` belongs to a local `User` (`Guid UserId`). Until Step 6 nothing created
users: the only identity was the authenticated principal's `sub` and `tid` claims. Reading
the code also turned up a latent cross-tenant bug. `Users` had a **global** unique index on
`ExternalId`, and `IUserRepository.FindByExternalIdAsync` took no tenant. A subject id is
unique only within one identity provider's issuer. Under the development scheme every
tenant presents `dev-user`. So the first user lookup would have resolved tenant B's caller
to tenant A's row.

## Decision

- **Provision on first contact.** `UserProvisioner.GetOrProvisionAsync` finds the caller by
  `(TenantId, ExternalId)`, or creates the `User` from the principal's `email` (falling back
  to `preferred_username`) and `name`. The new row is saved immediately.
- **Uniqueness and lookup are per tenant:** the index becomes `UX_Users_TenantId_ExternalId`,
  and the repository method takes a `TenantId`.
- **Reads never provision.** GET endpoints call `FindAsync`. A caller with no local user has
  no conversations: 404, or an empty list.
- **No invented email.** If the principal carries none, the answer is 403
  `user.incomplete_identity`. A deactivated user gets 403 `user.deactivated`.
- The development scheme now issues `email` (`<subject>@dev.invalid`, a reserved domain) and
  `name` claims.

## Consequences

### Positive

- Conversations, audit trails and Step 9's permissions get a stable local key that this
  database owns. An identity-provider migration rewrites one column, not every foreign key.
- Step 12's real identity provider plugs into the same path unchanged.
- The cross-tenant collision is closed, and two integration tests guard it.

### Negative / costs we accept

- **A first-contact race:** two concurrent *first* requests from one person both miss the
  lookup, and the unique index rejects the second insert as a 500. The index makes the race
  safe, not invisible. A retry succeeds.
- Profile data (email, name) is copied at first contact and never refreshed. Refreshing it
  is Step 12's job.

## Alternatives considered

- **Key conversations on the raw subject string.** Less code now. It wins only in a system
  that never needs a local user list, roles or permissions, and this one needs all three by
  Step 9.
- **Provision in authentication middleware on every request.** It wins if almost every
  endpoint needs the local user. It costs a lookup on every request, documents included,
  and makes reads write.
- **SCIM / directory sync ahead of first sign-in.** It wins when an administrator must
  grant access before a person has ever signed in, and is the right Step 12+ answer for a
  real enterprise.

## Revisit when

- Step 12 introduces a real identity provider (refreshing profile claims, deprovisioning).
- The first-contact race shows up in logs at a rate worth a retry-on-conflict.
