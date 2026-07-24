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
  avoided a read: a pointer task's `read_tokens` is exactly the read it still owes.

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
| explain-cs-envelope | Explain | CSharp | 0.00 | 1.00 | 1.00 | 0.631 | 1.00 | 1.00 | 1.000 | 1985 | 381 | 0 | none |
| locate-ts-login | Locate | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 353 | 0 | 259 | none |
| fix-ts-session-validity | Fix | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 0.176 | 726 | 120 | 0 | none |
| explain-ts-session | Explain | TypeScript | 1.00 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 828 | 111 | 0 | none |
| impact-ts-session | Impact | TypeScript | 0.50 | 1.00 | 1.00 | 1.000 | 1.00 | 1.00 | 1.000 | 350 | 0 | 259 | none |

## Simulated workflow accounting

Layers are reported separately so a saving in one cannot be counted twice.

| task | calls | core | cli stdout | mcp content | mcp transport | session | args | full reads | full-read tokens | model-visible CLI | wire MCP |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| locate-cs-packer | 1 | 645 | 642 | 643 | 701 | 1146 | 25 | 1 | 1008 | 1675 | 3523 |
| fix-cs-budget | 1 | 2158 | 2159 | 2159 | 1128 | 1146 | 23 | 0 | 0 | 2182 | 4456 |
| explain-cs-envelope | 1 | 1985 | 1984 | 1986 | 2069 | 1146 | 22 | 0 | 0 | 2006 | 5223 |
| locate-ts-login | 1 | 353 | 356 | 353 | 448 | 1146 | 22 | 1 | 116 | 494 | 2085 |
| fix-ts-session-validity | 1 | 726 | 724 | 726 | 681 | 1146 | 24 | 0 | 0 | 748 | 2577 |
| explain-ts-session | 1 | 828 | 826 | 826 | 996 | 1146 | 21 | 0 | 0 | 847 | 2989 |
| impact-ts-session | 1 | 350 | 350 | 350 | 441 | 1146 | 20 | 2 | 259 | 629 | 2216 |

## Reuse economics

Query: `change budget packing` at slices detail, top 3.

| | core tokens | content tokens | results | reused |
| --- | ---: | ---: | ---: | ---: |
| first call | 1906 | 756 | 3 | 0 |
| repeat with receipts | 609 | 49 | 1 | 4 |
