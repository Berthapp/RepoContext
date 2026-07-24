# Release 1 candidate evaluation manifest

This manifest makes the current deterministic evaluation snapshot auditable. It
does **not** establish a pre-Release-1 before/after comparison: the fixture,
labels, harness and implementation were introduced in the same uncommitted
change. Once this candidate is committed, it becomes the frozen baseline for
later changes.

## Provenance

| Field | Value |
| --- | --- |
| Base commit | `9986548ee7c4640d42a2f89ab03e6dd96aaa4982` (`claude/token-savings-dashboard`) |
| Source state | uncommitted Release 1 candidate; the base commit alone cannot reconstruct it |
| OS | Windows 11 Pro 10.0.26200 (x64) |
| Runtime / SDK | .NET SDK 10.0.301, target `net10.0` |
| Build configuration | `Release` |
| Tokenizer | `Microsoft.ML.Tokenizers` 1.0.2 + `Microsoft.ML.Tokenizers.Data.O200kBase` 1.0.2, encoding `o200k_base` |
| Usage ledger | disabled (`REPOCTX_NO_STATS=1`) |
| Runtime network | none; the evaluation is in-process and the production projects ban network APIs |

Token counts are exact local BPE counts of the declared rendered boundary. No
character/token approximation and no LLM, embedding service or network call is
used.

## Corpus and labels

- Fixture: `tests/fixtures/eval-repo/`.
- Labels: `tests/RepoContext.Integration.Tests/Evaluation/EvalCorpus.cs`.
- Seven C# and TypeScript tasks across `Locate`, `Explain`, `Fix` and `Impact`.
- Every task uses a declared 3,000-token response budget.
- Include roots: `src`, `tests`, `vendor`; `.env` is a forbidden sensitive path.

The corpus is a candidate frozen with this implementation, not a retroactive
quality comparator. It currently lacks JavaScript/TSX, multi-project,
required-scaffolding and explicit dependency/test-edge labels. The impact task
requires both affected files but does not score relation kind or direction.

Frozen line labels:

| Symbol | File | Lines |
| --- | --- | ---: |
| `Budget` | `src/Packing/Packer.cs` | 115-139 |
| `EnvelopeTokens` | `src/Packing/Packer.cs` | 145-148 |

After the candidate is committed, product changes may not edit labels merely to
pass a gate.

## Reproduction

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:REPOCTX_NO_STATS = '1'

dotnet build RepoContext.slnx -c Release
dotnet test tests/RepoContext.Integration.Tests -c Release --no-build `
  --filter "FullyQualifiedName~Evaluation"

# Rewrite aggregate/raw snapshots only when their diff is being reviewed.
$env:REPOCTX_UPDATE_EVAL_BASELINE = '1'
dotnet test tests/RepoContext.Integration.Tests -c Release --no-build `
  --filter "FullyQualifiedName~BaselineSnapshotTests"
```

`BaselineSnapshotTests` verifies the aggregate report, every expected raw
artifact, absence of obsolete raw files and byte-identical repeated rendering.
Raw artifacts become independently source-reproducible once this candidate is
committed; until then, preserve the working-tree diff with the review.

## Counted boundaries

| Layer | Boundary |
| --- | --- |
| `core_document_tokens` | rendered core result, without CLI newline or transport |
| `cli_stdout_tokens` | exact stdout, including its trailing newline |
| `mcp_content_tokens` | model-visible MCP text content block |
| `mcp_transport_tokens` | deterministic canonical JSON-RPC response-envelope model, excluding request ID |
| `session_overhead_tokens` | production server instructions plus generated `ProtocolTool` schemas, counted once |
| `call_argument_tokens` | exact serialized MCP argument object |
| `full_read_tokens` | indexed BPE cost of deterministic full-file reads required by policy |

The MCP session and envelope artifacts are canonical local models built from
the production instructions/tool schemas. They are not captured SDK
initialization, tool-list or call frames, so they must not be described as an
exact wire transcript.

Cold, no-op and one-file-change index scenarios record deterministic bytes read,
files parsed, graph files analyzed and edges recomputed. `IndexStats` and
`repoctx index` expose elapsed milliseconds, but timing is not persisted in the
golden because it is machine-dependent; isolated median/p95 timing remains a
separate benchmark task.

## Mechanical workflow policy

`WorkflowSimulator` has `MaxSteps = 5` and:

1. issues the task's declared context request;
2. searches symbols only if a must-find file is absent;
3. requests an outline only if a required symbol is absent;
4. requests `related` for an impact task only while a must-find file is absent;
5. reads every file explicitly labelled `FullReadExpected`, plus any file whose
   required span was not delivered;
6. deduplicates full reads and stops when the labelled evidence proxy is complete.

The policy now accounts for declared reads correctly, but the current seven
tasks normally complete their RepoContext portion in one call. It therefore
does not yet provide strong coverage of repeated escalation behavior or
dependency-edge correctness.

## Metric formulas

- `recall@k` = must-find files in the first *k* results / must-find files.
- `ndcg@8` = DCG over graded label position / ideal DCG.
- `symbol_recall` = delivered must-find symbols / labelled symbols.
- `span_recall` = fully covered labelled ranges / labelled ranges.
- `density` = delivered lines inside labelled ranges / all delivered source
  lines, accepted only with full span recall.
- `gap` reports missing labelled symbol/span evidence after the first call.
- Per-task `read` is the projected cost of all delivered pointers; workflow
  `full-read tokens` counts only files the frozen policy actually reads.

## Evidence classification

| Claim | Status |
| --- | --- |
| Current `baseline.md` totals and `raw/` bodies | **reproduced within this source state** by the commands above |
| Current first/repeat receipt costs | **reproduced**; both raw core bodies are preserved |
| Release 1 versus pre-change relevance change ≤1 percentage point | **not established**; no pre-change frozen baseline exists |
| Audit prototype 1,235 → 561 span-selection saving | **hypothesis/history only**; no preserved before artifact |
| Future output-compaction estimates | **hypothesis** until Release 2 artifacts exist |

## Known limitations

- This is a small static evidence proxy, not proof that an arbitrary model will
  produce a correct patch.
- It lacks explicit edge precision/recall, scaffolding recall, JavaScript/TSX
  and multi-project strata.
- The baseline cannot validate before/after Release 1 quality because it was not
  frozen before the implementation.
- MCP transport is a canonical model rather than a captured SDK transcript.
- Cross-platform normalized golden validation is supplied by CI for Linux,
  Windows and macOS; local validation in this review was performed on Windows.
- All processing remains local, deterministic and explainable; no repository
  data or usage data leaves the process through RepoContext.
