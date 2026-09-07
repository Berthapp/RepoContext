# Review follow-up — remaining implementation and evidence

Follow-up to the [original review](functional-effectiveness-2026-09-06.md) and
[first correction report](functional-corrections-2026-09-07.md). Historical raw
measurements remain unchanged. This work adds missing dependency diagnostics,
project scoping, compact output and reproducible retrieval/scaling evaluation.

## Implementation

- `context --format json --compact` and MCP `get_context(compact: true)` opt into
  schema v5, removing deprecated compatibility fields and the duplicate single
  source-span text. Default JSON stays v4. Both representations use the actual
  renderer for hard response budgeting and preserve evidence receipts across
  formats. The [contract decision](../decisions/0021-compact-context-json.md)
  lists the fields and identity semantics.
- C# dependency resolution uses indexed project ownership and literal SDK
  project references to narrow duplicate type candidates. Ambiguous or
  unsupported cases remain explicit. Unambiguous syntax relationships survive
  ordinary shared build-file imports whose MSBuild evaluation is unavailable.
- `related` and MCP `get_related_files` expose unresolved TS/C# dependency
  references with a reason, and identify C# relationships as syntax inference.
  Edges and diagnostics come from one committed read snapshot. The graph producer
  version invalidates older graph generations. The [graph decision amendment](
  ../decisions/0006-graph-and-context.md#september-2026-correction-local-resolution-and-visible-uncertainty)
  documents supported configuration and limits.
- README and token-accounting documentation link to the current generated
  evaluation rather than copying token figures. Calibrated counts, estimated
  replacement savings and actual rendered response costs are distinguished.

## Retrieval evidence

The [frozen corpus](../eval/holdout/README.md) contains 30 new source-inspected
tasks and the six historical diagnostic queries. Four complete pinned source
snapshots, upstream licenses, per-file hashes, relevant line ranges, required
relationships and independent source-role labels are checked in. Labels were
frozen before measurements; no ranking weights or query synonyms were tuned.

The harness measures the eligible candidate pool, unbudgeted top-eight selection,
budgeted JSON/compact/Markdown delivery, relevant source-line coverage and actual
oracle-assisted full-file reads. Rendering the same evidence in multiple formats
separates serialization cost from changes in selected evidence. Regression gates
protect the particular required files, source lines and graph edges previously
found for each task. Response errors cannot count as successful retrieval.

The initial corpus run exposed an overconservative project-scope check: an
ordinary `Import` in shared build settings suppressed existing unambiguous C#
dependencies. That regression was corrected before final measurement. The
[initial result](../eval/holdout/initial.json) remains separate from the
[current baseline](../eval/holdout/baseline.json).

| Final measurement | New 30 tasks | Historical six tasks |
| --- | ---: | ---: |
| Required files in eligible candidates | 38/38 | 11/11 |
| Required files, unbudgeted top 8 | 38/38 | 9/11 |
| Required files, JSON / compact JSON at 2,000 tokens | 29/38 | 7/11 |
| Required source lines, JSON / compact JSON at 2,000 tokens | 204/449 | 78/191 |

All 11 labeled dependency occurrences are indexed. Compact JSON reduces the cost
of identical unbudgeted evidence by 11.5% across the corpus, but does not improve
aggregate budgeted recall. On the historical tasks, the cost model and reference
extractor are now both candidate rank 9 (selection loss); German FileScanner and
Receipt are ranks 5 and 3 (packing loss). The proposed 90% tight-budget file-recall
target remains unmet. The corpus report explicitly records historical ranking
regressions caused by restoring correct graph relationships, alongside improved
line coverage on the new tasks. This provides a reproducible basis for later
packing work instead of another adjustment to the six development queries.

A two-pass packing experiment did reach 36/38 (94.7%) compact JSON file recall,
but cut required-line coverage from 204 to 115 and increased oracle follow-up
body tokens from 43,184 to 53,734. It was rejected and the original packer
restored. The [experimental data and patch](../eval/holdout/README.md#packing-experiment-rejected-by-line-coverage)
remain available; passing the file-recall target alone would have hidden a
substantial loss of useful evidence.

## Verification

The Release solution build succeeds with no warnings. The complete .NET suite
passes **427 core and 242 integration tests (669 total)**; all **12 npm launcher
tests** pass. After the rejected packing experiment, a full rebuild restored
the exact CLI/core assembly hashes recorded in the retained holdout baseline;
the four corpus/legacy-snapshot checks passed again without update variables.

The existing seven-task golden preserves all file/symbol/span recall metrics.
Its small response-token changes reflect the new graph identity; MCP session
declarations grow from 1,641 to 1,662 tokens, below the existing 1,700-token
gate. The refreshed baseline and raw output diffs were reviewed separately from
the immutable historical audit files.

New regressions cover compact CLI/MCP output, exact and calibrated response
ceilings, retry budgets and cross-format receipts; literal/transitive C# project
references, ambiguous owners/types and shared build imports; TS unresolved and
invalid configuration diagnostics; and frozen corpus source integrity, specific
file/line/edge retention and complete response accounting.

## Repeatable performance measurements

The [scaling probe](scaling_probe.py) generates 300-, 3,000- and 30,000-file
C#/TypeScript repositories with many competing matches. It runs sequential
warm-ups and repeated CLI calls, rotates case order, records raw responses and
checks exact output tokens using a separate local tokenizer helper. It reports
median and nearest-rank p95; at five repetitions p95 is the sample maximum.
Timings include fresh process/CLR startup against a warmed index/filesystem.
They are not persistent MCP latency or a comparison with the original Linux run.

```powershell
dotnet build RepoContext.slnx -c Release
dotnet test RepoContext.slnx -c Release --no-build
node --test npm/repocontext/test/platform.test.mjs
# Stop builds/tests before measuring latency.
python docs/reviews/scaling_probe.py `
  --cli src/RepoContext.Cli/bin/Release/net10.0/repoctx.dll `
  --output ../repoctx-scaling-results.json --repetitions 5 --compact `
  --skip-candidate-control
```

The skip option applies only to exhaustive synthetic candidate listings;
budgeted queries still run at every requested scale. The frozen real-repository
corpus measures candidate recall separately without this shortcut. New result
files are created exclusively; failures preserve partial evidence.

The [recorded scaling run](scaling-2026-09-07.json) completed all 105 timed
queries. All 108 budgeted outputs, including warm-ups, passed independent token
recounting; repeated stdout was deterministic. Both synthetic expected files
were present in every timed response. Windows x64, .NET SDK 10.0.301; five samples
per case, times below in seconds as **median / sample p95**:

| Files | JSON, 1,000 tokens | Compact JSON, 1,000 | JSON, 2,000 | Compact JSON, 2,000 |
| ---: | ---: | ---: | ---: | ---: |
| 300 | 0.969 / 1.022 | 0.921 / 0.932 | 1.085 / 1.097 | 0.636 / 0.673 |
| 3,000 | 2.669 / 2.791 | 2.681 / 2.838 | 3.340 / 3.359 | 1.294 / 1.298 |
| 30,000 | 13.077 / 14.809 | 13.866 / 14.907 | 18.606 / 19.471 | 4.980 / 5.135 |

At 30,000 files the unbudgeted median is 4.993 seconds; Markdown at 2,000 tokens
is 4.800 seconds. Large-repository latency is now measured and remains a
limitation. The compact format's benefit depends on budget and selected content;
it is slightly slower than default JSON at 1,000 tokens on this large fixture.

The [separate self-repository run](self-repository-2026-09-07.json) used a clean
checkout of the original source commit `806ba51e24aed14904b799dd01430c9f78672b47`
(362 indexed files), the same final candidate binaries, and five repetitions per
case. All 120 timed queries completed; all 108 bounded outputs, including
warm-ups, passed exact token checks. Default and compact JSON both return 7/11
expected file occurrences. At 2,000 tokens:

| Original audit task | JSON median / p95 | Compact JSON median / p95 |
| --- | ---: | ---: |
| Ignore files, English | 2.038 / 2.338 | 2.092 / 2.523 |
| Ignore files, German | 1.359 / 1.394 | 1.261 / 1.334 |
| Receipt reuse | 2.123 / 2.349 | 2.147 / 2.493 |
| Response budget | 1.894 / 1.953 | 1.805 / 1.972 |
| Import resolution | 2.039 / 2.109 | 2.008 / 2.018 |
| Session persistence | 1.804 / 1.818 | 1.657 / 1.889 |

The suggested sub-two-second target is not met uniformly. These Windows
process timings must not be treated as a controlled speedup over the original
single Linux measurements. The no-op index analyzes zero graph files and
recomputes zero edges. Reproduce with `--repo <clean-pinned-checkout> --scales
--budgets 2000 --repetitions 5 --compact` and a new output path; `--scales` without
values disables synthetic generation for this separate run.

## Remaining experimental limits

The new tasks are retrieval questions authored from source inspection, not
independently adjudicated bug reports. The external JavaScript/TypeScript
libraries are small and from one author; two have only one main implementation
file. Relevant-line coverage therefore matters more than file recall alone.
Next.js applications and a second C# repository are not represented.

A paired model-driven patch experiment was attempted through the installed
Codex CLI. Its preflight failed with the account's usage-limit error before
any coding task ran. No model success rate, completed-patch comparison or
end-to-end agent cost improvement is claimed. That experiment still requires
available model capacity, identical tasks/model/settings in both arms, repeated
runs, independently executed success tests and complete usage transcripts.

Syntactic resolution still does not evaluate arbitrary MSBuild, Roslyn semantic
binding, package exports or package-based TS configuration inheritance. Freshness
cannot make external filesystem edits into an atomic repository snapshot.
