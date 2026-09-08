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
| cold | 16456 | 6 | 4 | 6 |
| no-op | 8228 | 0 | 0 | 0 |
| one-file-change | 8909 | 1 | 4 | 6 |

## Per-task metrics

| task | class | lang | r@1 | r@3 | r@8 | ndcg@8 | sym | span | density | core | content | read | gap |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| locate-cs-packer | Locate | CSharp | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 649 | 0 | 1541 | none |
| fix-cs-budget | Fix | CSharp | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 0.316 | 2174 | 805 | 0 | none |
| explain-cs-envelope | Explain | CSharp | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 2014 | 381 | 0 | none |
| locate-ts-login | Locate | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 357 | 0 | 259 | none |
| fix-ts-session-validity | Fix | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 0.176 | 713 | 120 | 0 | none |
| explain-ts-session | Explain | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 833 | 111 | 0 | none |
| impact-ts-session | Impact | TypeScript | 0.50 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 355 | 0 | 259 | none |

## Simulated workflow accounting

Layers are reported separately so a saving in one cannot be counted twice.

| task | calls | core | cli stdout | mcp content | mcp transport | session | args | full reads | full-read tokens | model-visible CLI | wire MCP |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| locate-cs-packer | 1 | 649 | 650 | 649 | 706 | 1662 | 25 | 1 | 1008 | 1683 | 4050 |
| fix-cs-budget | 1 | 2174 | 2173 | 2174 | 1131 | 1662 | 23 | 0 | 0 | 2196 | 4990 |
| explain-cs-envelope | 1 | 2014 | 2015 | 2014 | 2094 | 1662 | 22 | 0 | 0 | 2037 | 5792 |
| locate-ts-login | 1 | 357 | 357 | 356 | 453 | 1662 | 22 | 1 | 116 | 495 | 2609 |
| fix-ts-session-validity | 1 | 713 | 713 | 713 | 683 | 1662 | 24 | 0 | 0 | 737 | 3082 |
| explain-ts-session | 1 | 833 | 830 | 831 | 997 | 1662 | 21 | 0 | 0 | 851 | 3511 |
| impact-ts-session | 1 | 355 | 356 | 353 | 442 | 1662 | 20 | 2 | 259 | 635 | 2736 |

## Reuse economics

Query: `change budget packing` at slices detail, top 3.

| | core tokens | content tokens | results | reused |
| --- | ---: | ---: | ---: | ---: |
| first call | 1916 | 756 | 3 | 0 |
| repeat with receipts | 621 | 49 | 1 | 4 |
