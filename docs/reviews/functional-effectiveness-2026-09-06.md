# Functional and effectiveness analysis of RepoContext

As of 6 September 2026. The analyzed version is **v0.10.0**, commit
`806ba51e24aed14904b799dd01430c9f78672b47` on `main`.
Working branch: `analysis/functional-effectiveness`.

> Originally written in German; translated to English on 2026-09-12. The
> findings, figures and query strings are unchanged — the German task strings in
> section 1 are the exact inputs that were passed to the CLI and are kept
> verbatim.

## Assessment

**RepoContext runs. The decisive need for improvement is in reliably selecting
helpful context, and in response time.**
The product already has the essential functions: local search, source excerpts,
symbols, relationships, change detection, reuse, agent integration and a local
usage report. More file formats or additional surfaces are currently worth less
than improvements to these core flows.

All existing tests passed. Additional counter-probes nevertheless find wrong or
missing dependencies, a bug with reduced configurations, and weak results on
naturally phrased tasks. The existing token figures demonstrate individual
saving mechanisms, but not yet better or cheaper handling of real programming
tasks.

This branch contains the analysis, the measurement data and a reproduction
script. **Product corrections are proposed work below; they are not implemented
here.**

## What was actually checked

| Check | Result |
| --- | --- |
| Release build of the whole solution | Succeeded |
| .NET core tests | 375 passed, 0 failed, 0 skipped |
| .NET integration tests including MCP and evaluation | 218 passed, 0 failed, 0 skipped |
| npm launcher | 12 passed |
| GitHub CI of the analysis branch on the starting commit | Succeeded, [run 34025987352](https://github.com/Berthapp/RepoContext/actions/runs/34025987352) |
| Full index of the tool's own repository | 362 files, 3,516 chunks, 2,972 symbols, 1,681 edges; 3,091 ms internally |
| Unchanged follow-up index | 0 files re-parsed; 340 graph files and 1,681 edges nevertheless reprocessed; 263 ms internally |
| Additional functional probes | TypeScript imports, C# name collisions, empty configuration, changes after indexing |
| Search against the tool's own repository | 6 tasks labelled in advance from the code; further isolated control measurements |

Local environment: Linux x64, .NET SDK 10.0.100 / runtime 10.0.0, Release build.
The solution was run with the bundled MSBuild; the usual `dotnet build` /
`dotnet test` commands are the corresponding reproduction paths.
The files and results come from real CLI invocations. No LLM tasks were run.
Timings are single measurements, not p95 benchmarks.
The first search series ran in parallel with integration tests; the control
measurements explicitly marked as isolated below ran afterwards without them.

## 1. Highest priority: secure hit quality on real tasks

For each task, the expected implementation files were determined from the code
before the CLI was invoked. Each invocation was:

```sh
repoctx context "<task>" --detail auto --top 8 --response-budget-tokens 2000 --format json
```

| Task, passed to the CLI unchanged | Expected files in the answer |
| --- | ---: |
| `exclude generated files and respect gitignore` | 1 of 2 |
| `Generierte Dateien ausschliessen und Gitignore beachten` | 1 of 2 |
| `avoid sending the same source spans twice using receipts` | 0 of 2 |
| `fix response token budget packing` | 1 of 2 |
| `resolve TypeScript module imports to source files` | 0 of 2 |
| `persist and load agent session receipts` | 1 of 1 |

In total, **4 of 11 expected file hits** were delivered. Only one of the six
tasks had all its determined files in the first answer.
This is a small diagnostic test, not a representative success rate for the
product, and not evidence that the remaining tasks are unsolvable.
Additional searches or file reads can close the gaps, but they cost further
work.

The import task is especially illustrative. `GraphBuilder.cs` and
`ReferenceExtractor.cs` are required. The budgeted answer instead contains
`ILanguageParser.cs`, `GraphTests.cs` and `DetailPolicyTests.cs`.
The eight results without a response limit do not contain the two expected files
either. A targeted search for `ResolveTsImport`, by contrast, finds
`GraphBuilder.cs` immediately. Indexing and symbol search work here; selection
for the naturally phrased task is the problem.

**Recommended change:** create an independent task corpus with 30–50 real tasks
from C#, TypeScript/Next.js and several projects. Record target files, decisive
code ranges and necessary relationships before optimizing. Deliberately
distinguish production code, tests, test fixtures and documentation; fixtures
must not silently take the place of the implementation for work on the real
product. Treat high-frequency query terms such as `files`, `source` or `change`
as less dominant. Measure ranking and budget effects separately and improve them
against that corpus.

**Acceptance:** report recall before and after, delivered relevant lines,
required follow-up calls and full task completion. One possible initial target
is at least 90 % file recall at top 8 on the separately determined corpus; this
is a proposal, not a value reached so far.

Code: [QueryAnalyzer](../../src/RepoContext.Core/Context/QueryAnalyzer.cs),
[ContextEngine: candidates, ranking, diversity and pack](../../src/RepoContext.Core/Context/ContextEngine.cs).

## 2. Highest priority: make budgeting markedly faster

Isolated control measurement of the same import task on the same index:

| Variant | Total CLI process duration | Expected implementation files |
| --- | ---: | ---: |
| JSON, response limit 2,000 | 22.494 s | 0 of 2 |
| Markdown, response limit 2,000 | 16.825 s | 0 of 2 |
| JSON, without `--response-budget-tokens` | 1.768 s | 0 of 2 |
| JSON, 2,000, plus `--path src/RepoContext.Core` | 3.297 s | 0 of 2 |
| Symbol search `ResolveTsImport` | 0.208 s | `GraphBuilder.cs` found |

The explicit response limit makes this query much more expensive. Removing the
limit improves runtime in this case, but does not solve the hit gap. It should
therefore not be the product's answer.

**Plausible cause from the code, not yet isolated with a profiler:**
`SelectCandidates` repeatedly calls `ClassifyOmissions` over the entire
candidate set for proposed variants. Variants are rebuilt and tokenized again in
`SpanVariant`. On top of that, `ContextCostModel.Measure` renders and tokenizes
complete prospective answers. With hundreds of candidates this creates
substantial repeated work.

**Recommended change:** cache variants and immutable token costs per query,
carry omission metadata forward efficiently, and reduce expensive response
checks. Keep tokenizing the actually emitted result exactly and checking it
against the hard limit. Do not use naive addition of partial BPE costs as a
substitute for the final check.

The JSON output also delivers the same source text twice for single spans, in
`spans[].text` and in the compatibility field `snippet`. An explicitly versioned
compact output mode could remove that redundancy and make room for relevant
evidence. Existing clients must be taken into account.

**Acceptance:** isolated measurements with several repetitions on 300, 3,000 and
30,000 files, with tight budgets and many hits. Existing budget and receipt
tests remain binding. Under two seconds for warm queries on the tool's own
repository is a sensible first product target; not yet measured, not a
guarantee.

Code: [ContextEngine.SelectCandidates/ClassifyOmissions/SpanVariant](../../src/RepoContext.Core/Context/ContextEngine.cs),
[ContextCostModel.Measure](../../src/RepoContext.Cli/Output/ContextCostModel.cs),
[ContextOutput](../../src/RepoContext.Cli/Output/ContextOutput.cs).

## 3. Confirmed bug: missing configuration fields lose the defaults

A `repoctx.config.json` containing `{}` does not activate the same settings as
`repoctx init`. In the test, `.env`, `node_modules/demo/index.js` and
`obj/generated.cs` were indexed. Those files contained dummy text only. With the
defaults written by `init` they should have been excluded.

Cause: `ConfigStore.Deserialize` deserializes into `RepoctxConfig`. Its property
initializers set `Exclude` and `SensitiveFiles` to empty lists. The intended
defaults exist only in `CreateDefault()`. Missing JSON fields never call that
factory.

This is both a functional quality gap through unnecessary data volume and a
trust problem for the exclusion of sensitive files. It does not imply any
observed network transmission: RepoContext itself works locally.

**Small, clearly bounded correction:** use uniform default values for new
instances and for `CreateDefault()`. Missing fields adopt the defaults;
explicitly supplied empty lists remain a deliberate override. In addition,
validate invalid `null` values and negative size limits with a clear message.
Cover the regression for `{}`, a partial configuration and explicit lists, each
through the actual scan/index pipeline.

Code: [RepoctxConfig](../../src/RepoContext.Core/Configuration/RepoctxConfig.cs),
[ConfigStore.Deserialize](../../src/RepoContext.Core/Configuration/ConfigStore.cs).

## 4. Important functional gap: TypeScript dependencies

Minimal probe with `src/session.ts` and three calling files:

| Import | Recognized by `related src/session.ts` as a caller? |
| --- | --- |
| `./session` | Yes |
| `./session.js` | No |
| `@/session`, with `paths: { "@/*": ["./src/*"] }` | No |

TypeScript supports resolving `.js` imports to TypeScript sources and configured
path mappings; see the
[official module reference](https://www.typescriptlang.org/docs/handbook/modules/reference.html#file-extension-substitution)
and the [paths documentation](https://www.typescriptlang.org/tsconfig/paths.html).
In `ResolveTsImport`, RepoContext merely appends extensions to the unchanged
import path. Non-relative paths are ignored beforehand. The missing alias
resolution is already documented as an MVP limit in ADR 0006.

**Impact:** `related`, change impact and graph-supported context can miss actual
callers and tests. The files generally remain findable through text and symbol
search.

**Recommended order:** first map `.js`/`.mjs`/`.cjs` to matching source and
declaration files; then `tsconfig` path mappings including `extends` and project
boundaries. Report unsupported or ambiguous imports visibly as unresolved. Cover
the existing relative control case and negative cases without a local target as
well.

Code: [GraphBuilder.AddImportEdges/ResolveTsImport](../../src/RepoContext.Core/Graph/GraphBuilder.cs).

## 5. Confirmed wrong relationships: identically named C# types

Two independent files are enough:

```csharp
// src/A/User.cs
namespace Alpha;
public sealed class User { public string Name => "A"; }

// src/B/User.cs
namespace Beta;
public sealed class User { public string Name => "B"; }
```

`repoctx related src/A/User.cs --format json` reports `src/B/User.cs` both as
`imports` and as `imported_by`. No such dependency exists between these classes.
The declared name `User` is itself extracted as a type usage; during resolution
the file's own declaration is skipped and the other identically named class is
chosen instead.

**Recommended change:** separate declarations from actual usages, do not treat
comments and strings as real type usages, and take namespace/project information
into account. Mark uncertain name heuristics in the output. For ambiguous types,
do not silently assert a real import relationship. A full Roslyn adapter can
follow later; this simple failure case should be closed before that.

**Acceptance:** no edges between the two independent classes; actual qualified
usages, identical names across several projects and existing test links remain
correct.

Code: [ReferenceExtractor](../../src/RepoContext.Core/Graph/ReferenceExtractor.cs),
[GraphBuilder.AddTypeEdges](../../src/RepoContext.Core/Graph/GraphBuilder.cs).

## 6. Known operational limit: context stays stale after changes

After indexing, `createSession()` was changed from `OLD_SESSION` to
`NEW_SESSION`. The next identical context query returned the old source text
**byte for byte**. `changed --patch` detected the change; after `index` the new
content appeared.

This is the documented separation between index queries and working-directory
inspection, not a newly discovered violation of the current contract. For
reliable agents it is nevertheless an important operational trap: a branch
switch, or a change made by another process, can also make the index stale
before the agent itself edits anything.

**Recommended change:** offer a simple freshness strategy for the start of a
task and for branch switches, e.g. an opt-in `--ensure-fresh` or a refresh
driven by the integration. Make the index generation, or unverified freshness,
visible. An MCP client without a shell can currently call `get_changes`, but has
no corresponding index tool; a complete refresh path is missing for that use
case.

Until then: index before the task, use `changed --patch` after changes, and
re-index when needed. A watcher is a possible later addition, but need not be
the first implementation step.

Code: [CommandSupport.EnsureIndexUsable](../../src/RepoContext.Cli/Commands/CommandSupport.cs),
[ChangeDetector](../../src/RepoContext.Core/Indexing/ChangeDetector.cs),
[McpTools](../../src/RepoContext.Cli/Mcp/McpTools.cs).

## 7. Additional reliability risk: index state is not atomic

**A code finding, not reproduced by a forced process abort:**
The indexer writes files in one transaction, then builds the graph, and updates
metadata individually afterwards. `ClearEdges` sits outside the graph
transaction. Reading the existing files happens before the write transaction,
and an overarching indexer lock is missing.

An abort or concurrent index runs can therefore leave contradictory intermediate
states behind. `HasValidStateHash` only checks the format of the stored hash,
not that it matches the current data. In addition, the file hash and the indexed
text are read separately; a change in between can let content and identity
diverge.

**Recommendation:** serialize index runs per repository, derive the hash and the
analysis from the same read content, and publish a complete index generation
atomically. Reading queries should use a consistent generation. Consider this
fixed only with abort, concurrency and change probes; a lock alone does not
replace atomic publication.

Code: [Indexer.Run](../../src/RepoContext.Core/Indexing/Indexer.cs),
[GraphBuilder.Rebuild](../../src/RepoContext.Core/Graph/GraphBuilder.cs),
[IndexStore](../../src/RepoContext.Core/Storage/IndexStore.cs).

## 8. Prove the effect instead of only counting smaller answers

The existing evaluation is transparent about its limits: six files, seven static
C#/TypeScript tasks and one simulated strategy. The currently stored receipt
probe drops from **1,912 to 607 core tokens** on repetition. That is concrete
evidence of cheaper reuse, but not a comparison of a whole programming task with
and without RepoContext.

`stats` credits source excerpts and outlines against assumed replaced full-text
reads. If an agent reads the file in full afterwards anyway, that assumption
does not hold. The output should distinguish estimated savings clearly from
measured response costs. A blanket token factor for other model families is also
a calibration, not an exactly identical tokenizer.

A fair next effectiveness test needs:

1. The same predefined tasks and the same success tests with and without
   RepoContext; as a comparison, ordinary symbol/text search plus targeted
   reads.
2. Total model-visible tokens: instructions, tool definitions, arguments,
   answers and actual follow-up reads. Keep MCP transport bytes separate; they
   are not automatically additional model input.
3. Correct patches or passing task tests, failed attempts, follow-up calls and
   runtimes. Lower cost counts as an improvement only together with unchanged
   result quality.
4. For a later model trial, an identical model and identical settings, several
   repetitions, and success rates per task type.

Documentation to follow up: the README still names 1,904 → 609 tokens;
`docs/token-savings.md` still seven tools and 1,339 session tokens. The current
golden contains 1,912 → 607 as well as eight tools and 1,602 session tokens.
Generate such figures from the evaluation where possible, or link to the current
report.

Sources: [baseline](../eval/baseline.md), [manifest](../eval/manifest.md),
[UsageMeter](../../src/RepoContext.Core/Stats/UsageMeter.cs),
[WorkflowSimulator](../../tests/RepoContext.Integration.Tests/Evaluation/WorkflowSimulator.cs).

## Recommended implementation

| Package | Concrete outcome | Completion criterion |
| --- | --- | --- |
| A: small corrections | uniform configuration defaults, C# declaration bug, `.js` import resolution | the counter-probes above green as regression tests |
| B: main benefit | independent task corpus, better selection, less repeated tokenization | measurably more required evidence at the same budget and a markedly shorter response time |
| C: reliable operation | consistent index generation, freshness path, MCP refresh, alias resolution | tasks after an edit or branch switch, and parallel index runs, are reliable |
| D: evidence | complete comparison without/with RepoContext, clearly labelled estimates | equal or better task results at less total effort |

The first technical step should be package A; the most important product
improvement is package B. The architecture, offline operation, SQLite and
deterministic processing can all stay as they are.

## Reproduction and measurement files

- [Measurement data with complete CLI answers](functional-effectiveness-2026-09-06.json)
- [Reproduction script](functional_effectiveness_probe.py), Python 3 and an
  already-built RepoContext; standard library only, no additional Python
  packages.

To keep this analysis report from influencing the search results itself, check
out the commit under investigation separately. The script creates dummy fixtures
in a temporary directory and rebuilds the local index of the given working copy.
It changes no product files.

```sh
git worktree add --detach ../RepoContext-audit 806ba51e24aed14904b799dd01430c9f78672b47
dotnet build ../RepoContext-audit/RepoContext.slnx -c Release
dotnet test ../RepoContext-audit/RepoContext.slnx -c Release --no-build
node --test ../RepoContext-audit/npm/repocontext/test/platform.test.mjs
python3 docs/reviews/functional_effectiveness_probe.py --repo ../RepoContext-audit --output ../RepoContext-audit-results.json
```

Measure runtimes separately from the tests. The original raw measurements are
kept unchanged for this analysis; later runs are new observations.
