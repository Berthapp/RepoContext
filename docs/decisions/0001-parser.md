# ADR 0001 — Parser: tree-sitter via TreeSitter.DotNet

- **Status:** accepted (pending milestone approval)
- **Date:** 2026-07-08
- **Milestone:** M0 (parser spike — hard stop)

## Context

RepoContext needs to extract symbols (classes, functions/methods, interfaces,
type aliases, enums, exported arrow functions, properties) from TypeScript, TSX,
JavaScript and C#. M0 requires a Go/No-Go decision between tree-sitter and a
fallback (Roslyn for C# + tree-sitter for TS/JS), with these Go criteria:

1. Both languages work.
2. Average parse+extract < 10 ms/file across the fixtures.
3. Runs locally and in Linux CI.
4. A clear native-library packaging path for `linux-x64`, `win-x64`, `osx-arm64`.

The hard part for tree-sitter on .NET is packaging the **native grammars**: each
grammar is a separate native library that must exist per RID.

## Candidates

| Package | Version | Size | Notes |
|---|---|---|---|
| **TreeSitter.DotNet** (Marius Greuel, MIT) | 1.3.0 | 51 MB | Bundles `libtree-sitter` core **and 28+ grammars** (incl. `typescript`, `tsx`, `javascript`, `c-sharp`) as `runtimes/<rid>/native/*` for win/linux/osx × x86/x64/arm/arm64. `netstandard2.0`, zero dependencies. Clean `Language`/`Parser`/`Query` API. |
| TreeSitterSharp | 0.5.0 | 28 KB | Bindings only — no bundled grammars; we would have to compile and package every grammar per RID ourselves (the exact hard problem above). |
| tree-sitter | 0.4.19 | 16 KB | Core bindings only; same grammar-packaging burden. |
| Roslyn (`Microsoft.CodeAnalysis.CSharp`) | 5.6.0 | — | Fallback for C# only; managed, no native deps, high fidelity. Not needed for MVP syntactic extraction. |

## Spike and measurements

`spikes/parser/` parses both fixture projects with **TreeSitter.DotNet 1.3.0**
and extracts symbols with tree-sitter queries. Run on `linux-x64`
(.NET 10.0.109), framework-dependent (no RID specified — native assets resolved
automatically from the package's `runtimes/` folder):

| Language | Files | Symbols | Avg ms/file | Max ms |
|---|---|---|---|---|
| TypeScript | 12 | 25 | 0.529 | 3.900 * |
| TSX | 4 | 6 | 0.229 | 0.317 |
| JavaScript | 2 | 1 | 0.078 | 0.095 |
| C# | 14 | 34 | 0.247 | 0.554 |
| **Overall** | **32** | **66** | **0.340** | — |

\* The 3.9 ms outlier is the first file (one-time native load + grammar init).
Even including it, the overall average is **0.340 ms/file — ~30× under the
10 ms/file budget.**

Extraction was verified correct against the fixtures: TS/JS classes, functions,
exported arrow functions, interfaces, type aliases, enums, methods; C# classes,
interfaces, records, structs, methods, properties, enums. Grammar node-type
differences (JavaScript has no `interface`/`type_alias`/`enum` nodes and uses
`identifier` for class names) require a **per-grammar query**, which is handled.

## Decision

**GO with tree-sitter via `TreeSitter.DotNet` 1.3.0 for all four grammars**
(TypeScript, TSX, JavaScript, C#). All four Go criteria are met.

Roslyn is **not** used in the MVP. It is deferred to a post-MVP semantic adapter
for C# (already planned as v0.4 in the product doc) where real reference
resolution is needed; MVP C# needs only syntactic symbols and name-based edges.

## Packaging plan

- **Distribution:** a single NuGet dependency provides native assets for every
  required RID. Framework-dependent native load was verified working on
  `linux-x64`; `dotnet global tool` and self-contained per-RID binaries both
  resolve `runtimes/<rid>/native/` automatically. CI (Linux) is covered.
- **Binary size:** the package ships 28+ grammars (~51 MB). For the M4 release
  binaries, add an MSBuild target that trims `runtimes/*/native/libtree-sitter-*`
  down to `libtree-sitter` + `{typescript, tsx, javascript, c-sharp}` for the
  target RID. Tracked as an M4 packaging task; not required for M1–M3.
- **Abstraction:** Core exposes a single `ILanguageParser` (per the build
  prompt) so the binding stays behind an interface. No plugin system.
- **Risk / mitigation:** single-maintainer binding. Mitigations: it is MIT
  licensed (vendorable), pinned to an exact version, and the `ILanguageParser`
  seam plus a parser smoke test keep us able to swap implementations.

## Consequences

- M2 implements `ILanguageParser` with a tree-sitter adapter and the queries
  prototyped here (promoted from the spike; the spike code itself is throwaway
  and never referenced by `src/`).
- A parser smoke test in CI guards that the native grammars load on the CI RID.
