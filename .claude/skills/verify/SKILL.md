---
name: verify
description: Run the EKA quality gate — restore, build with warnings-as-errors, and the full test suite — then report what actually passed. Use before committing a step, when asked to check the build, or when diagnosing a build or test failure in this repository.
---

# Verify the build

The quality gate for this repository. Run it before committing any step.

## Commands

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --nologo
```

`dotnet build` alone is usually enough during development; use the full sequence before a
commit.

## What counts as passing

- **Build: zero warnings.** `Directory.Build.props` sets `TreatWarningsAsErrors`, so a
  warning fails the build by design. Never fix this by lowering the setting — suppress a
  specific diagnostic deliberately, with a comment explaining why.
- **Tests: all green, every project reporting.** As of Step 1 there are 19 tests across
  four projects. "No test is available in ..." means a test project has no tests — that is
  a gap to fill, not noise to ignore.

Report results faithfully. If something fails, show the output. If a step was skipped, say
so. Do not describe a partially verified build as verified.

## Known failure modes in this repository

**`NU1507` — multiple package sources.** Central Package Management requires unambiguous
source resolution. This machine has several NuGet feeds configured globally; `nuget.config`
pins the repository to nuget.org with explicit source mapping. If this reappears after a
feed is added, map the new feed to its own package ID prefix. **Never** silence it with
`NoWarn` — the ambiguity it reports is the dependency-confusion attack vector. See
`docs/adr/0006-package-source-mapping.md`.

**`NU1008` — version on a PackageReference.** Package versions live only in
`Directory.Packages.props`. Remove the `Version=` attribute from the csproj and add a
`<PackageVersion>` entry instead.

**Architecture test failures.** A test in `tests/*/`*BoundaryTests.cs* or
`ArchitectureTests.cs` failing means a project reference now points outward, breaking the
dependency rule. Fix the design, never the test: move the shared type inward, or declare
an interface in `EKA.Application` and implement it in the outer project. The matrix is in
`docs/concepts/clean-architecture.md`.

**SDK mismatch.** `global.json` pins SDK 10.0.403 with `rollForward: latestFeature`. If
the SDK is missing, install it rather than loosening the pin — the pin exists because this
machine has multiple SDKs including a preview.

## Later steps add dependencies

From Step 3 onward some integration tests need containers (SQL Server, Qdrant, Redis).
If those fail, check Docker is running before assuming the code is broken:

```powershell
docker ps
```
