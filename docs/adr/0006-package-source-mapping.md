# ADR-0006: Pin NuGet to nuget.org with package source mapping

- **Status:** Accepted
- **Date:** 2026-11-11
- **Deciders:** project owner

## Context

Enabling Central Package Management ([ADR-0005](0005-central-package-management.md))
immediately failed the build with **NU1507**:

> There are 2 package sources defined in your configuration. When using central package
> management, map your package sources with package source mapping or specify a single
> package source.

This machine has three feeds configured at user level:

| Feed | Location |
|---|---|
| nuget.org | `https://api.nuget.org/v3/index.json` |
| SBC Nuget (internal) | `https://sbcnuget.sbcsdaia.com/nuget` |
| Visual Studio offline packages | `C:\Program Files (x86)\Microsoft SDKs\NuGetPackages\` |

NU1507 is not a style complaint. With several feeds and no mapping, NuGet may resolve a
package ID from **any** of them, which is the **dependency-confusion** attack: an
attacker publishes a package to a public feed using the same ID as a victim's internal
package, and the victim's restore pulls the attacker's code — executed at build time,
with the developer's credentials. It has been demonstrated repeatedly against large
organizations, which is why CPM escalates the ambiguity to an error.

A second, duller problem: relying on machine-level feeds means a teammate or CI agent
without the SBC feed configured gets a different restore, or none at all.

## Decision

Add a committed repository-local `nuget.config` that:

1. `<clear />`s inherited feeds, so a build here depends only on what the repository
   declares — identical on every machine and in CI.
2. Declares **nuget.org only**.
3. Declares an explicit `<packageSourceMapping>` with `*` mapped to nuget.org.

Every dependency this project needs through Step 14 is public, so no internal feed is
required. If an internal package is adopted later, the feed is declared **with a mapping
limited to its own ID prefix** (e.g. `Sbc.*`) — `*` is never mapped to two sources.

Credentials for any private feed stay in the user-level NuGet config or a CI secret,
never in this committed file.

## Consequences

### Positive

- Dependency confusion is structurally impossible: each package ID has exactly one
  permitted source.
- Restores are reproducible across machines and CI regardless of user-level NuGet state.
- The failure mode is loud — an unmapped package ID fails restore instead of silently
  resolving from an unexpected feed.
- The internal feed remains available to every other project on the machine; this change
  is scoped to this repository.

### Negative / costs we accept

- Adding an internal package later requires editing `nuget.config`, not just installing
  it. That friction is the point.
- The Visual Studio offline folder is no longer consulted here, so first restore on a new
  machine needs network access.
- One more committed config file to keep current.

## Alternatives considered

**Suppress NU1507** (`<NoWarn>NU1507</NoWarn>` or dropping `TreatWarningsAsErrors`).
Rejected: it silences the warning while leaving the vulnerability, and the warning exists
precisely because the default is unsafe.

**Declare both feeds with full source mapping now.** The more complete answer if internal
packages were in use. Rejected for now because this project consumes none, and mapping a
feed we never pull from adds a CI dependency on an internal host for no benefit. The ADR
records how to add it when that changes.

**Leave machine-level config alone and disable CPM.** Rejected — it trades a real
supply-chain control for a version-drift problem, losing on both counts.

## Revisit when

- The project needs a package from the SBC feed — add the feed with a prefix-scoped
  mapping, never a `*` mapping.
- CI is introduced in Step 14 — confirm the agent honours this file and consider adding
  `packages.lock.json` with `RestoreLockedMode` for fully pinned transitive restores.
