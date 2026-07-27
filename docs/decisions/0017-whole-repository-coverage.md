# ADR 0017 — Whole-repository coverage by default

- **Status:** accepted
- **Date:** 2026-07-27
- **Release:** 0.8.0 (no JSON `schema_version` change; index rebuild required)

## Context

`repoctx init` wrote `include: ["src", "app", "lib", "docs"]`. Include roots are
resolved against the repository root and are not searched at any other depth, so
the default only ever indexed a repository whose code sits in one of those four
top-level directories.

Everything else was silently unindexed:

- **A root holding several projects.** Open a folder with two checkouts —
  `apps/web`, `services/api` — run `init` at the top, and the scan selects
  **zero files**. `repoctx index` reports `files: 0` and exits 0, because a scan
  that matches nothing is not an error.
- **Conventional layouts that are not `src/`.** The repository's own
  `tests/` tree, a .NET repository laid out as `Controllers/`, `Services/`,
  `Models/` (the `sample-cs` fixture), `packages/`, `tools/`, `cmd/`.
- **`indexing.includeTests: true` could not do what it says.** A top-level
  `tests/` directory was outside the roots, so the setting had no effect there.
  The evaluation corpus had to override `include` to work around this.

The failure is silent and total, which is the worst shape for a tool whose value
proposition is "ask RepoContext instead of reading files": an agent gets an empty
or partial index and no signal that anything is missing.

A second, related gap: ignore files were read only at the repository root. In a
multi-project checkout the root frequently has no `.gitignore` at all, and each
project's own ignore rules were disregarded.

## Decisions

### 1. The default scope is the whole repository

`include` defaults to `[]`, which the scanner already reads as "scan `.`". Every
subdirectory below the root is walked, at any depth, so every project and every
subfolder is indexed without configuration.

Coverage becomes **subtractive** — excludes, ignore files, sensitive patterns,
size limit, binary detection — rather than an allow-list of root directories.
Subtractive rules fail visibly (a named directory is skipped) instead of
silently (an unnamed directory was never in scope).

`include` keeps its meaning when set: an explicit list still narrows the scan,
each entry still scanned recursively. Narrowing is now an opt-in for people who
want it, not the default everyone gets.

### 2. Default excludes cover generated output at any depth

The exclude list is extended to the build-output directory names that are
generated in practically every ecosystem (`build`, `out`, `target`, `.nuxt`,
`.svelte-kit`, `.turbo`, `.angular`, `.gradle`, `.venv`, `venv`, `__pycache__`,
`.pytest_cache`, `.mypy_cache`, `.tox`, `coverage`, `.idea`, `.vs`, alongside
the existing `node_modules`, `dist`, `bin`, `obj`, `.next`, `.git`).

A bare name is matched by basename at any depth, so one entry covers every
project in the repository.

`vendor/` is deliberately **not** excluded. Vendored source is real code that is
occasionally the answer; ADR 0006 already ranks it down with an explicit vendor
penalty, and suppressing it at scan time would remove a signal the ranker is
built to handle.

### 3. Ignore files are read per directory

`.gitignore` and `.repoctxignore` are now read in every directory the scan
enters, not just at the root. Each file's patterns are matched against paths
relative to its own directory, and scopes are evaluated outermost-first so the
innermost rule that matches decides — gitignore's own precedence, including `!`
re-inclusion. At the same level `.repoctxignore` is evaluated after
`.gitignore`, so the RepoContext-specific file wins.

This required a third state in the matcher: `GitignoreMatcher.Match` returns
`null` when no rule applies, so an outer decision survives an inner non-match.
Without it, stacking matchers with `||` would make every negation unreachable.

Sensitive patterns are checked before the ignore stack and are never
re-includable by a nested file. A `!.env` in a subdirectory cannot pull a
sensitive file into the index.

### 4. Detected projects are reported, never persisted

`ProjectDetector` maps marker files (`package.json`, `*.csproj`, `go.mod`,
`Cargo.toml`, `pyproject.toml`, `pom.xml`, `composer.json`, `Gemfile`,
`CMakeLists.txt`, …) to project roots, one per directory, ordered by path.

It exists only so `init` can print what the user is getting:

```
  scope: the whole repository, 412 file(s) selected
  projects: 2 detected
    apps/web (node, package.json)
    services/api (dotnet, SampleApi.csproj)
```

Detection deliberately drives **nothing** else. It does not write include roots,
does not scope queries and does not group results — a directory that belongs to
no project is indexed exactly like one that does. Detection heuristics that
influence coverage would reintroduce the silent-miss failure this ADR removes.

### 5. Existing configurations are warned about, not rewritten

A config written by an older `init` keeps its explicit `include` list and its
behavior. `repoctx index` writes a warning to stderr when a configured include
root does not exist, or when the roots select no files at all:

```
Warning: configured include root(s) do not exist: src, app, lib, docs
  Only src, app, lib, docs is scanned, so code elsewhere in this repository
  (including nested projects) is not indexed.
  Remove "include" from repoctx.config.json (or set it to []) to index the
  whole repository, then re-run 'repoctx index'.
```

Silently rewriting a user's configuration would discard deliberate narrowing.
The warning is on stderr and the exit code stays 0, so scripts are unaffected.

### 6. Console output is pinned to UTF-8

Found while testing this change: on Windows the CLI encoded stdout with the
active console code page, which best-fit-maps characters the page lacks — under
CP 850 the em dash in `prime`'s header silently became a hyphen. Identical index
and identical query therefore did not produce byte-identical output, violating
the project's determinism constraint. The CLI now sets `Console.OutputEncoding`
and `InputEncoding` to UTF-8 without BOM at startup, which also matches what the
MCP stdio transport requires.

## Consequences

- **Indexes get bigger.** More files are scanned, parsed and stored. On this
  repository the file count went from the `src`/`docs` subset to 311 files;
  incremental indexing keeps subsequent runs cheap. Repositories that want the
  old scope set `include` explicitly.
- **`init` now scans.** It walks the repository to report scope and projects,
  making `init` roughly as expensive as one scan pass. It is a one-time command
  immediately followed by `index`, which does strictly more work.
- **Config hash changes.** The default exclude list is part of
  `ComputeIndexHash`, so the first `index` after upgrading is a full rebuild.
- **Evaluation baseline moved by a few tokens.** Config-hash-derived identifiers
  (`analysis_state`, `evidence_id`, `representation_id`) are rendered into
  responses, and different hash text tokenizes to a slightly different count.
  Every relevance metric in `docs/eval/baseline.md` — `r@1`, `r@3`, `r@8`,
  `ndcg@8`, symbol/span recall, density, `gap` — is unchanged, and the corpus
  itself is byte-identical: `EvalRepo` dropped its `include` workaround and the
  plain default now selects the same six files.

## Deviation from the product doc

`repocontext-produktdoku.md` chapter 14 shows `"include": ["src", "app", "lib",
"docs"]` in its example configuration. That example is not reproduced by the new
default. The product doc stays unedited (it is binding and not to be modified);
this ADR records the deviation and its reason: the example configuration
conflicts with the product doc's own principle that RepoContext answers
questions about *the repository*, and as a default it produced an empty index
for a large class of ordinary layouts.

## Relation to the plan

This supersedes the coverage half of work package **Q6** in
`docs/cost-efficiency-implementation-plan.md` ("Repository coverage discovery
and `doctor`"), which proposed deterministic root *detection* at `init` time.
Detection was rejected as the coverage mechanism for the reason in decision 4:
it can be wrong, and when it is wrong it fails silently in exactly the way this
ADR set out to fix. Scanning everything cannot miss a root.

The read-only `repoctx doctor` command proposed by Q6 — unindexed supported
files, shadowed settings, stale index, coverage estimate — is **not** part of
this change and remains open.
