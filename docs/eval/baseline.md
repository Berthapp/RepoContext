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
| locate-cs-packer | Locate | CSharp | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 645 | 0 | 1541 | none |
| fix-cs-budget | Fix | CSharp | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 0.316 | 2158 | 805 | 0 | none |
| explain-cs-envelope | Explain | CSharp | 0.00 | 1.00 | 1.00 | 0.631 | 1.00 | 1.00 | 1.000 | 1981 | 381 | 0 | none |
| locate-ts-login | Locate | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 355 | 0 | 259 | none |
| fix-ts-session-validity | Fix | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 0.176 | 727 | 120 | 0 | none |
| explain-ts-session | Explain | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 828 | 111 | 0 | none |
| impact-ts-session | Impact | TypeScript | 0.50 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 353 | 0 | 259 | none |

## Simulated workflow accounting

Layers are reported separately so a saving in one cannot be counted twice.

| task | calls | core | cli stdout | mcp content | mcp transport | session | args | full reads | full-read tokens | model-visible CLI | wire MCP |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| locate-cs-packer | 1 | 645 | 643 | 645 | 701 | 1339 | 25 | 1 | 1008 | 1676 | 3718 |
| fix-cs-budget | 1 | 2158 | 2157 | 2159 | 1128 | 1339 | 23 | 0 | 0 | 2180 | 4649 |
| explain-cs-envelope | 1 | 1981 | 1981 | 1982 | 2069 | 1339 | 22 | 0 | 0 | 2003 | 5412 |
| locate-ts-login | 1 | 355 | 353 | 354 | 447 | 1339 | 22 | 1 | 116 | 491 | 2278 |
| fix-ts-session-validity | 1 | 727 | 724 | 724 | 682 | 1339 | 24 | 0 | 0 | 748 | 2769 |
| explain-ts-session | 1 | 828 | 827 | 828 | 998 | 1339 | 21 | 0 | 0 | 848 | 3186 |
| impact-ts-session | 1 | 353 | 353 | 353 | 442 | 1339 | 20 | 2 | 259 | 632 | 2413 |

## Reuse economics

Query: `change budget packing` at slices detail, top 3.

| | core tokens | content tokens | results | reused |
| --- | ---: | ---: | ---: | ---: |
| first call | 1905 | 756 | 3 | 0 |
| repeat with receipts | 608 | 49 | 1 | 4 |
