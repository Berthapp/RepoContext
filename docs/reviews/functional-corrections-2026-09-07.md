# Functional corrections — 2026-09-07

Follow-up to [the original audit](functional-effectiveness-2026-09-06.md).

## Implemented

- Sparse configuration preserves default exclusions and sensitive-file patterns; malformed configuration fails validation.
- Local TS/JS import resolution supports extension substitution, index files, nearest tsconfig/jsconfig, paths/baseUrl and local extends (including JSONC).
- C# dependency facts come from syntax, excluding declarations, comments and string literals as references. Qualified names and namespace facts narrow matches; ambiguous matches stay unresolved.
- Context selection materializes candidate variants once per query; repeated budget fitting reuses them while retaining exact final response accounting.
- Ranking accounts for distinct query-term coverage and conservative English plurals, limits accumulated directory penalties, and reduces unrequested fixture/test/document noise. Outline selection avoids redundant enclosing declarations.
- File content and its persisted hash use the same byte buffer. Files, graph and metadata commit atomically; writers serialize and context reads use a database snapshot. Unchanged indexing skips graph reconstruction.
- CLI `context --ensure-fresh` and MCP `get_context(ensureFresh: true)` refresh before querying, including an initialized repository without an index. Generated agent guidance uses freshness explicitly.
- Savings displays identify estimates; graph, ranking and evidence producer versions invalidate affected stale identities.

## Verification

The Release solution build succeeded. The initial regression run passed 395 core tests and 219 of 220 integration tests. The only failure was the checked-in evaluation snapshot; its reviewed refresh preserves recall@8 for all seven fixture tasks, improves the C# envelope task at rank 1, and records actual additional bytes read. MCP session overhead is 1,641 tokens, below the existing 1,700-token gate. All 12 npm launcher tests passed. The final full-suite run passed all 395 core and 220 integration tests (615 .NET tests total), without the snapshot-update environment variable.

New regression tests cover sparse/invalid configuration, TS extensions and inherited aliases, false and qualified C# references, transaction rollback during both incremental and full indexing, concurrent writers, no-op graph reuse, plural guards, and CLI/MCP freshness.

## Repeated real-repository probes

[Raw results](functional-corrections-2026-09-07.json) were captured using the corrected Release CLI against the same clean source commit `806ba51e24aed14904b799dd01430c9f78672b47` as the original audit. The CLI SHA-256 is included. No tests ran concurrently with measurement. Historical timing is a single-run comparison, not a controlled performance distribution.

| Measure | Original audit | Corrected CLI |
| --- | ---: | ---: |
| Expected files returned, six tasks, auto/top 8/JSON/2,000 response tokens | 4/11 | 7/11 |
| Isolated import query, 2,000 response tokens | 22.494 s | 1.967 s |
| Same query without response ceiling | 1.768 s | 0.833 s |
| No-op graph files analyzed / edges recomputed | nonzero | 0 / 0 |

All three module fixtures (relative, `.js`, alias) now resolve. Sparse config excludes the dummy `.env`, node_modules and obj files. Duplicate C# declaration files no longer produce a false edge. Default queries still intentionally return the committed index until explicitly refreshed.

Reproduce against a clean checkout of the source commit, with the candidate built separately:

```sh
python3 docs/reviews/functional_effectiveness_probe.py \
  --repo /absolute/path/to/clean-baseline-checkout \
  --cli /absolute/path/to/candidate/src/RepoContext.Cli/bin/Release/net10.0/repoctx.dll \
  --dotnet /absolute/path/to/dotnet \
  --output /absolute/path/outside-baseline/results.json
```

## Next approach and limits

Four expected file occurrences are still missing under the tight JSON budget: FileScanner.cs in the German ignore task; Receipt.cs in the receipt task; ContextCostModel.cs in the budget task; ReferenceExtractor.cs in the import task. Separate candidate recall from selection/serialization loss before further ranking changes. Freeze a broader holdout set with source-inspected labels, including other repositories, before tuning.

C# resolution is syntactic, not Roslyn semantic binding. TS resolution covers indexed local configuration, not package exports or package-based extends. Freshness adds hashing I/O and cannot provide a repository-wide atomic filesystem snapshot while external edits are in flight. Large-repository scaling and real agent task completion/cost remain unmeasured. The seven fixture workflows and six self-repository tasks establish diagnostic evidence, not general coding-agent effectiveness.
