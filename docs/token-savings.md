# Token savings — measurement and accounting

RepoContext is useful only when an agent gathers the evidence for the same task
with less model-visible work and without losing relevant evidence. The Release
1 candidate therefore measures deterministic simulated evidence-gathering
workflows, not character counts or isolated snippets.

The current reviewable results are:

- `docs/eval/baseline.md` — aggregate relevance and cost metrics;
- `docs/eval/raw/` — exact core, CLI and MCP-content bodies plus deterministic
  canonical MCP envelope/session models used by those token totals; and
- `docs/eval/manifest.md` — corpus, policy, formulas, environment and exact
  reproduction commands.

All token figures use the local `o200k_base` tokenizer. RepoContext never calls
an LLM, uses embeddings, or sends code or measurements over a network.
Because this candidate corpus was introduced with the Release 1 implementation,
it is a baseline for later changes, not evidence of Release 1 versus pre-change
quality parity; the manifest records that provenance limitation explicitly.

## What is counted

The harness keeps each layer separate:

| Layer | Boundary |
| --- | --- |
| core document | rendered result without a CLI newline or transport wrapper |
| CLI stdout | exact stdout, including its trailing newline |
| MCP content | model-visible text content block |
| MCP transport | JSON-RPC escaping and response envelope |
| session overhead | the production server instructions and generated tool schemas, once per session |
| call arguments | actual serialized MCP argument objects |
| full-file reads | exact indexed token counts for reads required by the frozen workflow |

The report also records deterministic cold, no-op and one-file-change index
operation counters (bytes read, files parsed, graph files analyzed and edges
recomputed). Wall-clock timing is exposed by `repoctx index` but is not persisted
in the golden snapshot because it varies by machine.

## Budgets

The three budget options deliberately have different bases:

| Option | Basis | Hard ceiling? |
| --- | --- | --- |
| `--budget-tokens` | compatibility “charged work”: projected reads for paths, embedded evidence otherwise | yes, on that legacy basis |
| `--response-budget-tokens` | exact model-visible rendered response | yes |
| `--projected-read-budget-tokens` | downstream full-file reads implied by pointers | yes |

Only `--response-budget-tokens` bounds the serialized response. The CLI counts
its final newline; MCP counts the text content block and reports transport
overhead separately. Final omission and reuse metadata are included. If no
useful successful payload fits, the command emits no partial success and
returns an actionable `retry_budget_tokens`. That value is guaranteed to fit a
deterministically chosen compact useful payload; it may exceed the mathematical
minimum so the error path remains bounded on large repositories.

Active limits are echoed in the schema-v3 `budgets` object with explicit bases.

## Safe reuse

A short file hash identifies a file version; it does not prove that an agent
received the whole file. Release 1 separates the two claims:

- `--seen <receipt>` acknowledges exactly one previously delivered pointer,
  source span or outline symbol. Other evidence from the same file remains
  eligible.
- `--known <path>@<hash>` is an explicit assertion that the caller independently
  holds the entire file. Never derive it from a slice or outline response.

Receipts are stateless, deterministic full SHA-256 values encoded as base64url.
They bind the path, full content hash, representation kind, exact range/symbol,
canonical delivered evidence and only the producer versions relevant to that
unit. Reused units do not consume `--top` slots; their bounded metadata still
counts against the response budget.

## Usage dashboard semantics

`repoctx stats` reads a strictly local, git-ignored
`.repoctx/stats.jsonl`. It records actual response tokens. “Reads replaced” is
an estimate:

- an embedded non-empty slice or outline credits the file read it is assumed to
  replace;
- an explicit matching full-file `known` assertion can credit an avoided read;
- a span, symbol or pointer receipt receives no speculative full-file credit;
  it proves evidence possession, not that a full read was avoided; and
- discovery-only calls receive no replacement credit.

This conservative rule prevents mixed seen/unseen units from crediting the same
file twice. “Net saved” remains an estimate because whether an agent would
otherwise have read a file is counterfactual.

## Reproduce

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:REPOCTX_NO_STATS = '1'

dotnet build RepoContext.slnx -c Release
dotnet test tests/RepoContext.Integration.Tests -c Release --no-build `
  --filter "FullyQualifiedName~Evaluation"

# Rewrite aggregate and raw snapshots only when their diff is under review.
$env:REPOCTX_UPDATE_EVAL_BASELINE = '1'
dotnet test tests/RepoContext.Integration.Tests -c Release --no-build `
  --filter "FullyQualifiedName~BaselineSnapshotTests"
```

For an individual live call:

```powershell
repoctx index
repoctx context "improve token budget packing" `
  --detail slices --response-budget-tokens 2000 --format json
```

Tokenize the exact stdout with the same local tokenizer. Do not use `wc -c`,
bytes/4, or a characters-per-token approximation.
