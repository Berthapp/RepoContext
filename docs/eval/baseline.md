# RepoContext Release 1 evaluation baseline

Deterministic metrics over the frozen `eval-repo` corpus.
Token counts are exact `o200k_base` BPE counts of the rendered surface.

## Metric formulas

- `recall@k` = |must-find files in first k results| / |must-find files|
- `ndcg@8` = DCG over graded label position / ideal DCG, gain = |labels| - label index
- `symbol_recall` = |must-find symbols delivered| / |must-find symbols|
- `span_recall` = |labelled ranges fully covered by a delivered span| / |labelled ranges|
- `density` = delivered source lines inside a labelled range / all delivered source lines
- `core_tokens` = tokens of the rendered core document (no CLI newline, no transport)
- `content_tokens` / `read_tokens` = embedded evidence / projected full-file reads
- `gap` = whether any labelled symbol or span is still missing after one call.
  `none` on a pointer task means nothing labelled is missing, not that the agent
  avoided a read. Per-task `read_tokens` sums every delivered pointer's projected
  cost; the workflow table records only reads required by the frozen policy.

## Deterministic index operation counters

Wall-clock time is exposed by `IndexStats`/`repoctx index` but excluded from this machine-independent golden.

| scenario | bytes read | files parsed | graph files analyzed | edges recomputed |
| --- | ---: | ---: | ---: | ---: |
| cold | 16456 | 6 | 6 | 6 |
| no-op | 16456 | 0 | 6 | 6 |
| one-file-change | 16528 | 1 | 6 | 6 |

## Per-task metrics

| task | class | lang | r@1 | r@3 | r@8 | ndcg@8 | sym | span | density | core | content | read | gap |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| locate-cs-packer | Locate | CSharp | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 647 | 0 | 1541 | none |
| fix-cs-budget | Fix | CSharp | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 0.316 | 2163 | 805 | 0 | none |
| explain-cs-envelope | Explain | CSharp | 0.00 | 1.00 | 1.00 | 0.631 | 1.00 | 1.00 | 1.000 | 1986 | 381 | 0 | none |
| locate-ts-login | Locate | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 351 | 0 | 259 | none |
| fix-ts-session-validity | Fix | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 0.176 | 727 | 120 | 0 | none |
| explain-ts-session | Explain | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 831 | 111 | 0 | none |
| impact-ts-session | Impact | TypeScript | 0.50 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 354 | 0 | 259 | none |

## Simulated workflow accounting

Layers are reported separately so a saving in one cannot be counted twice.

| task | calls | core | cli stdout | mcp content | mcp transport | session | args | full reads | full-read tokens | model-visible CLI | wire MCP |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| locate-cs-packer | 1 | 647 | 645 | 644 | 701 | 1339 | 25 | 1 | 1008 | 1678 | 3717 |
| fix-cs-budget | 1 | 2163 | 2163 | 2161 | 1127 | 1339 | 23 | 0 | 0 | 2186 | 4650 |
| explain-cs-envelope | 1 | 1986 | 1986 | 1986 | 2070 | 1339 | 22 | 0 | 0 | 2008 | 5417 |
| locate-ts-login | 1 | 351 | 352 | 352 | 449 | 1339 | 22 | 1 | 116 | 490 | 2278 |
| fix-ts-session-validity | 1 | 727 | 726 | 730 | 682 | 1339 | 24 | 0 | 0 | 750 | 2775 |
| explain-ts-session | 1 | 831 | 831 | 832 | 997 | 1339 | 21 | 0 | 0 | 852 | 3189 |
| impact-ts-session | 1 | 354 | 356 | 357 | 442 | 1339 | 20 | 2 | 259 | 635 | 2417 |

## Reuse economics

Query: `change budget packing` at slices detail, top 3.

| | core tokens | content tokens | results | reused |
| --- | ---: | ---: | ---: | ---: |
| first call | 1904 | 756 | 3 | 0 |
| repeat with receipts | 609 | 49 | 1 | 4 |
