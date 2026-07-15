# Token savings — measured

RepoContext's value proposition is that an AI agent solves the same task for
fewer tokens. This page documents an end-to-end measurement of the M6
token-frugal protocol (ADR 0009/0010) on this repository itself, so the
numbers are reproducible: 75 files, 774 chunks, 536 symbols, 205 edges at the
time of measurement. All figures are real `o200k_base` BPE counts of the
bytes an agent would actually receive — not estimates.

For the same accounting applied automatically to *your* usage, run
`repoctx stats` — the token-savings dashboard aggregated from a local usage
log (ADR 0011).

**Task used throughout:** *"improve token budget packing in the context
engine"* (top-3 relevant files: `ContextEngine.cs` 3,256 tokens,
`ContextResult.cs` 882, `ContextCommand.cs` 1,198 — 5,336 together).

## Getting working context

| Agent workflow | Tokens received | What the agent has afterwards |
| --- | ---: | --- |
| grep/read: open the 3 relevant files | 5,336 | 3 whole files, no ranking, no reasons |
| `context` (pointers) + read all 3 | 886 + 5,336 = 6,222 | 3 whole files + ranking/reasons for 8 |
| `context --detail outline --budget-tokens 2000` | **2,151** | skeletons of 7 top files + reasons + hashes |
| `context --detail slices --budget-tokens 2000` | **2,110** | the 3 best source slices embedded + reasons + hashes |

The slices bundle answers most "where and what is this" questions directly —
**~60-66 % cheaper** than either full-read workflow, with explainable ranking
included. When a full read is still needed, its exact cost is already known
(`file_tokens`), so the agent spends deliberately.

## Budgets you can trust

`--budget-tokens` is charged at what the response actually costs (embedded
content in serialized form + per-item envelope), not at a guess:

| Bundle | Charged by the packer | Actual response | Accuracy |
| --- | ---: | ---: | ---: |
| slices @ 2,000 | 1,986 | 2,110 | 94 % |
| outline @ 2,000 | 1,991 | 2,151 | 93 % |

(The gap is the fixed document header.) The old bytes/4 heuristic was off by
~15 % on typical source (`ContextEngine.cs`: guessed 3,374, real 2,942) and
far more on indented or markdown-heavy files.

## Never pay twice

Re-running the same call with the previously returned hashes echoed back
(`--known <path>@<hash>` per file):

- the 3 already-delivered files come back as `unchanged: true` markers
  (~40 tokens each instead of ~650),
- the freed budget pulls in 5 additional files that had not fit before
  (response: 2,469 tokens of *new* information instead of 2,110 of repeats).

The `state` hash on every bundle tells the agent whether the index moved at
all between calls.

## Staying oriented cheaply

| Call | Tokens |
| --- | ---: |
| `architecture --depth 1 --format md` (session-start orientation) | 303 |
| `architecture --format md` (full depth-3 tree) | 505 |
| `outline` of the 3,256-token `ContextEngine.cs` | 1,111 |
| `changed` on a clean tree | 154 |

## Reproducing

```bash
repoctx index
repoctx context "improve token budget packing in the context engine" \
  --detail slices --budget-tokens 2000 --format json | wc -c   # ≈ 4 chars/token
```

Counts here were produced with the same `o200k_base` tokenizer the index
uses. Ranking or costing changes should re-run this measurement and update
the tables (see ADR 0010).
