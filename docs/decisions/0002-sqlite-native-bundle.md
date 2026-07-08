# ADR 0002 — Pin the patched SQLitePCLRaw native bundle

- **Status:** accepted
- **Date:** 2026-07-08
- **Milestone:** M-Skeleton

## Context

`Microsoft.Data.Sqlite` 10.0.9 transitively pulls
`SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which ships a native SQLite build with a
known high-severity advisory (GHSA-2m69-gcr7-jv3q). The whole 2.1.x line is
affected. With `TreatWarningsAsErrors=true`, the NuGet audit warning `NU1903`
fails the build, which is the desired behaviour — we do not want to ship a
vulnerable dependency.

## Decision

Add a direct dependency in `RepoContext.Core` on the patched
`SQLitePCLRaw.bundle_e_sqlite3` 3.0.3, which bundles a current, patched SQLite
(e_sqlite3 3.53.x) and overrides the vulnerable transitive package.

Rather than suppressing `NU1903` (`WarningsNotAsErrors` / `NuGetAudit=false`),
we keep the audit strict and fix the actual dependency.

## Consequences

- The native provider is a major version ahead of what
  `Microsoft.Data.Sqlite` references by default, so a runtime smoke test
  (`RepoContextInfoTests.Sqlite_NativeBundle_LoadsAndSupportsFts5`) verifies the
  provider loads and FTS5 works. This guards against a silent ABI/registration
  break on future bumps.
- If a future `Microsoft.Data.Sqlite` references a patched bundle itself, this
  explicit pin can be removed.
