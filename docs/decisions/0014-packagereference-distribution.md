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
- **Two MSBuild targets, auto-imported** from `build/`:
  - `RepoCtx` — runs the packaged CLI with `-p:RepoCtxArgs="..."`, working
    directory `$(MSBuildStartupDirectory)` (where the user invoked the
    build, i.e. normally the repo root), overridable via
    `-p:RepoCtxWorkingDirectory`.
  - `RepoCtxShim` — writes `repoctx` / `repoctx.cmd` wrapper scripts (default
    `.repoctx/bin/` under the startup directory, git-ignored with the rest of
    `.repoctx/`). The wrappers embed the absolute package path in the NuGet
    cache, giving humans, agent instructions and MCP configs a stable,
    quoting-free entry point; they must be regenerated after a package
    update, which the target's output says explicitly.
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
- Verified end-to-end on a net8.0 consumer: restore from a local feed,
  `-t:RepoCtx` (`--version`, `outline`), `-t:RepoCtxShim`, then
  `init`/`index`/`search` through the wrapper — native SQLite and
  tree-sitter libraries resolve from the package's `runtimes/` folder via
  `repoctx.deps.json`.
