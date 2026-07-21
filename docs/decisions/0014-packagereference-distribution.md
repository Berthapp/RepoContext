# ADR 0014 — PackageReference distribution (RepoContext.MSBuild)

- **Status:** accepted
- **Date:** 2026-07-21

## Context

RepoContext ships as a dotnet tool (`RepoContext.Tool`, ADR 0007) and as
self-contained binaries. Both are blocked in many corporate environments:
`dotnet tool install` (global *and* local, since local tools also run
through the tool-install machinery) is commonly on the blacklist, and
downloading release binaries even more so. What such environments *do* allow
is the NuGet feed their builds already restore from. Users asked for a way to
add repoctx to a .NET repository as a plain `PackageReference` — which the
DotnetTool package type rejects by design (`NU1212`/`NU1213`).

## Decisions

- **A third distribution, same binary.** A new packaging-only project
  `src/RepoContext.MSBuild` packs the CLI's framework-dependent publish
  output (no apphost; started via `dotnet exec repoctx.dll`) under
  `tools/net10.0/any/`, `runtimes/` natives included, with grammar trimming
  (ADR 0001/0007) forced on. Nothing new is compiled; CLI behaviour,
  contracts and versioning are shared with the other distributions.
- **`netstandard2.0` + `IncludeBuildOutput=false`.** The package carries no
  `lib/`, and its (empty) dependency group is satisfiable from any consuming
  TFM — a net472 or net8.0 repository can host the package; only the
  *machine* needs the .NET 10 runtime (the SDK used to build brings it).
  `DevelopmentDependency=true` makes `PrivateAssets=all` the consumer
  default, so the package never flows to their output or dependents.
- **Zero-command setup by default.** `RepoCtxAutoSetup` hooks
  `AfterTargets="Build"`: the first build after adding the package runs
  `repoctx init --agents` (config, `.gitignore` entry, `CLAUDE.md` /
  `AGENTS.md`), every build runs an incremental `repoctx index`, and the
  wrapper scripts are (re)written — `WriteOnlyWhenDifferent`, so they only
  change when the package path does, which also self-heals them after a
  package update. Guard rails: skipped for design-time builds
  (`$(DesignTimeBuild)`); in multi-TFM projects only the outer build runs it
  (thin `buildMultiTargeting/` imports + a condition excluding inner per-TFM
  builds), so init/index never race in parallel inner builds; repoctx
  failures are `WarnAndContinue` — the package must never break a consumer's
  build. Opt-outs per property: `RepoCtxAutoSetup=false` (all),
  `RepoCtxAutoAgents=false` (init with `--no-agents`),
  `RepoCtxAutoIndex=false`, `RepoCtxAutoShim=false`.
- **Repository-root detection** (`_RepoCtxResolveRoot`, shared by all
  targets, override `-p:RepoCtxRoot=...`): the nearest directory at or above
  the project holding a `repoctx.config.json`, else `$(SolutionDir)` (Visual
  Studio), else `$(MSBuildStartupDirectory)` (CLI builds from the repo
  root). `.git` cannot serve as the probe: MSBuild's
  `GetDirectoryNameOfFileAbove` only sees files, and `.git` is usually a
  directory.
- **Two MSBuild targets for manual control**, auto-imported from `build/`:
  - `RepoCtx` — runs the packaged CLI with `-p:RepoCtxArgs="..."` in the
    detected root, overridable via `-p:RepoCtxWorkingDirectory`.
  - `RepoCtxShim` — writes the `repoctx` / `repoctx.cmd` wrapper scripts
    (default `.repoctx/bin/` under the detected root, git-ignored with the
    rest of `.repoctx/`). The wrappers embed the absolute package path in
    the NuGet cache, giving humans, agent instructions and MCP configs a
    stable, quoting-free entry point.
- **Publish-into-pack.** The packaging project hooks
  `TargetsForTfmSpecificContentInPackage`, publishes the CLI into its own
  `obj/` and packs the result as `TfmSpecificPackageFile`. The MSBuild task
  passes `RemoveProperties="TargetFramework;RuntimeIdentifier"` so pack's
  `TargetFramework=netstandard2.0` global does not leak into the net10.0 CLI
  build. CI smoke-packs the project on every push; the release workflow packs
  and pushes it alongside `RepoContext.Tool` under the same version.

## Alternatives considered

- **Making `RepoContext.Core` a public library package**: does not solve the
  ask — agents consume the CLI/MCP surface, not a C# API — and would freeze a
  large internal API prematurely.
- **`DotnetCliToolReference`**: deprecated since SDK 2.2, not an option.
- **MSBuild `Task` assembly instead of an exe**: would run inside the build,
  but repoctx is an interactive/agent-driven CLI, not a build step; wrapping
  every command as task parameters would duplicate the whole CLI surface.

## Notes

- MSBuild item `Include` globbing ate a literal `%*` in the cmd wrapper; it
  is written MSBuild-escaped as `%25%2A` in the targets file.
- XML forbids `--` inside comments, so the shipped props/targets paraphrase
  CLI flags in their comments instead of spelling them out (a literal
  `--agents` in a comment made every consumer build fail with MSB4024).
- Packing the same props/targets files into both `build/` and
  `buildMultiTargeting/` via duplicate `None` items corrupts the package
  paths (`buildMultiTargeting//...`, error NU5129); the multi-targeting
  copies are therefore real files that just `<Import>` their `build/`
  siblings.
- Verified end-to-end on a net8.0 consumer and a `net8.0;netstandard2.0`
  multi-TFM consumer: restore from a local feed, first build auto-runs
  `init --agents` + `index` + shims (exactly once for multi-TFM), later
  builds re-index incrementally and stay silent, opt-out and design-time
  conditions hold, `-t:RepoCtx`/`-t:RepoCtxShim` work explicitly, and
  `search`/`outline`/`related` work through the wrapper — native SQLite and
  tree-sitter libraries resolve from the package's `runtimes/` folder via
  `repoctx.deps.json`.
