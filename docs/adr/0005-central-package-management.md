# ADR-0005: Central Package Management and a pinned SDK

- **Status:** Accepted
- **Date:** 2026-11-11
- **Deciders:** project owner

## Context

Eleven projects will share many packages — EF Core, Serilog, xUnit, FluentAssertions,
Microsoft.Extensions.AI. By default each `.csproj` declares its own version, and versions
drift: `EKA.Api` ends up on Serilog 4.1 while `EKA.Infrastructure` is on 4.3. The symptom
is a confusing runtime binding error or a behaviour difference that costs hours to trace.

Separately, this machine has three .NET SDKs installed (8.0.416, 10.0.100-preview.5,
10.0.403). Builds should not depend on which one happens to be selected.

## Decision

Three build-infrastructure files at the repository root:

**`global.json`** — pin the SDK:

```json
{
  "sdk": {
    "version": "10.0.403",
    "rollForward": "latestFeature"
  }
}
```

**`Directory.Packages.props`** — enable Central Package Management. Every version is
declared once here with `<PackageVersion>`; projects reference packages with no version
attribute.

**`Directory.Build.props`** — shared MSBuild properties for all projects:
`net10.0`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`,
`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, deterministic builds.

## Consequences

### Positive

- One place to see and change every dependency version; version drift becomes impossible
  rather than merely discouraged.
- A dependency upgrade is a one-line diff reviewers can actually assess.
- `rollForward: latestFeature` permits 10.0.x patch and feature-band updates while
  refusing an accidental .NET 11 or the installed preview SDK.
- Shared properties mean a new project inherits the right settings automatically — nobody
  forgets to enable nullable reference types on project nine.
- `TreatWarningsAsErrors` keeps the build honest; warnings that are allowed to accumulate
  are warnings nobody reads.

### Negative / costs we accept

- One more layer of indirection when reading a `.csproj` — the version is not in front of
  you.
- A transitive version conflict must be resolved centrally, occasionally needing
  `VersionOverride` for one project.
- `TreatWarningsAsErrors` will sometimes block a build for a warning you would rather defer.
  Mitigation: suppress a specific diagnostic deliberately, with a comment, rather than
  disabling the setting.
- `global.json` must be updated when the team moves to a new SDK, and a missing SDK version
  produces a hard failure. That failure is the point — it is better than a silent
  behaviour difference.

## Alternatives considered

**Per-project versions (the default).** Rejected: drift across eleven projects is a matter
of time, and the failure mode is expensive to diagnose relative to the near-zero cost of
CPM.

**No `global.json`.** Rejected: with three SDKs installed, including a preview, build
behaviour would depend on machine state. CI would differ from local.

**`Directory.Build.targets` instead of `.props`.** Different purpose — `.props` is imported
*before* project content so projects can override it; `.targets` is imported after. Shared
defaults belong in `.props`. We may add a `.targets` later for shared build steps.

**A central NuGet lock file (`packages.lock.json`).** Deferred. It gives fully
reproducible restores including transitive versions, which is valuable for CI. Adds churn
on every dependency change; worth enabling in Step 14 alongside the CI pipeline.

## Revisit when

- The CI pipeline lands in Step 14 — enable `packages.lock.json` and
  `RestoreLockedMode` then.
- .NET 11 becomes the target, requiring a `global.json` bump and a `TargetFramework` change
  in `Directory.Build.props`.
