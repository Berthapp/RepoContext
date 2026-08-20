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
| cold | 8228 | 6 | 4 | 6 |
| no-op | 8228 | 0 | 4 | 6 |
| one-file-change | 8264 | 1 | 4 | 6 |

## Per-task metrics

| task | class | lang | r@1 | r@3 | r@8 | ndcg@8 | sym | span | density | core | content | read | gap |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| locate-cs-packer | Locate | CSharp | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 646 | 0 | 1541 | none |
| fix-cs-budget | Fix | CSharp | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 0.316 | 2158 | 805 | 0 | none |
| explain-cs-envelope | Explain | CSharp | 0.00 | 1.00 | 1.00 | 0.631 | 1.00 | 1.00 | 1.000 | 1978 | 381 | 0 | none |
| locate-ts-login | Locate | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 352 | 0 | 259 | none |
| fix-ts-session-validity | Fix | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 0.176 | 716 | 120 | 0 | none |
| explain-ts-session | Explain | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 830 | 111 | 0 | none |
| impact-ts-session | Impact | TypeScript | 0.50 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 356 | 0 | 259 | none |

## Simulated workflow accounting

Layers are reported separately so a saving in one cannot be counted twice.

| task | calls | core | cli stdout | mcp content | mcp transport | session | args | full reads | full-read tokens | model-visible CLI | wire MCP |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| locate-cs-packer | 1 | 646 | 646 | 648 | 701 | 1602 | 25 | 1 | 1008 | 1679 | 3984 |
| fix-cs-budget | 1 | 2158 | 2156 | 2156 | 1127 | 1602 | 23 | 0 | 0 | 2179 | 4908 |
| explain-cs-envelope | 1 | 1978 | 1980 | 1977 | 2070 | 1602 | 22 | 0 | 0 | 2002 | 5671 |
| locate-ts-login | 1 | 352 | 353 | 354 | 449 | 1602 | 22 | 1 | 116 | 491 | 2543 |
| fix-ts-session-validity | 1 | 716 | 715 | 717 | 681 | 1602 | 24 | 0 | 0 | 739 | 3024 |
| explain-ts-session | 1 | 830 | 829 | 831 | 997 | 1602 | 21 | 0 | 0 | 850 | 3451 |
| impact-ts-session | 1 | 356 | 354 | 352 | 442 | 1602 | 20 | 2 | 259 | 633 | 2675 |

## Reuse economics

Query: `change budget packing` at slices detail, top 3.

| | core tokens | content tokens | results | reused |
| --- | ---: | ---: | ---: | ---: |
| first call | 1905 | 756 | 3 | 0 |
| repeat with receipts | 603 | 49 | 1 | 4 |
