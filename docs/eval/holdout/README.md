# Frozen retrieval diagnostics

[The manifest](manifest.json) freezes **30 new source-inspected retrieval tasks**
and a separate **six-query historical development set**, with exact source line
ranges, rationales, required import relationships and source-role metadata. Its
SHA-256 is `bfc0a7174b0a31da48ff5709e0e120a6014289b7714061db6e3209a7c3b45fa9`.
The labels were written before running this corpus. No ranking was tuned on the
results below. Historical audit queries are explicitly excluded from the new-task
aggregate.

## Sources and scope

The `sources/` archives contain complete, pinned git source snapshots. Each
archive includes its upstream MIT license. Archive and individual file SHA-256
values are recorded in the manifest and checked by the tests. Source bytes are
unchanged; the three external archives have their GitHub directory prefix removed
and ZIP timestamps normalized. No upstream dependency installation or code
execution is needed.

| Repository | Pinned source | New tasks | Historical tasks |
| --- | --- | ---: | ---: |
| RepoContext | [`806ba51e`](https://github.com/Berthapp/RepoContext/tree/806ba51e24aed14904b799dd01430c9f78672b47) | 6 C# | 6 C# |
| p-limit 6.2.0 | [`92381134`](https://github.com/sindresorhus/p-limit/tree/923811344705a6604967540e19f7653752021438) | 6 JavaScript | 0 |
| p-retry 6.2.1 | [`d067fea3`](https://github.com/sindresorhus/p-retry/tree/d067fea3c806351e4b1c3be90439bb0da638322c) | 6 JavaScript | 0 |
| p-queue 8.1.0 | [`6b84d8a4`](https://github.com/sindresorhus/p-queue/tree/6b84d8a4079fdc3f77a6eb8f2a985629990c731e) | 12 TypeScript | 0 |

Each snapshot is extracted and indexed independently with the current default
configuration. The labels and reports are outside the indexed repositories;
upstream tests, fixtures, documentation and configuration remain present as real
retrieval competitors. Required excerpts are production source. The manifest
records roles independently of the product's classifier.

This extends the seven tiny synthetic workflows; it does not turn them into
real-agent evaluations. These new tasks were authored by source inspection, not
sampled from issue trackers or independently adjudicated. The three external
projects are small asynchronous JavaScript/TypeScript libraries from one author;
p-limit and p-retry each have one main implementation file. File recall there is
much easier than in a large application, and line coverage is more informative.
Next.js applications, a second C# repository, large external repositories, model
task completion and end-to-end agent costs remain unmeasured.

## Measurement

The harness uses the current in-process engine and real CLI renderers/tokenizer:

1. **Eligible candidates:** paths, no budgets, `top = indexed file count`.
   This observes every positive-scoring eligible file after scoring. The product
   exposes the total pre-selection candidate count and nonpositive count, but
   not the identities of rejected nonpositive candidates. A missing eligible path
   therefore cannot be attributed exclusively to initial candidate generation.
2. **Selection:** slices, top 8, no response ceiling. A required file in the
   eligible pool but outside this result is a ranking/selection loss.
3. **Delivery:** slices, top 8, 2,000 response tokens, separately for default JSON,
   compact JSON and Markdown. Per-file ranks and lost/gained paths identify
   packing effects, including lower-ranked files that fit when others do not.
   The report also renders the *same unbudgeted evidence* on all three surfaces
   to isolate serialization cost from changed selection.
4. **Relevant lines:** the intersection of the union of labeled `(path, line)`
   pairs and the union of delivered source spans. Overlapping labels/spans count
   once. Pointers and outline signatures do not earn source-line credit.
   Required-line coverage and total delivered lines are separate denominators.
5. **Oracle-assisted follow-ups:** for every required file with undelivered
   labeled lines, the harness actually reads that entire file once and counts
   its bytes and decoded-body BPE tokens. Labels choose the file, so these are
   oracle-assisted read operations, not observed agent search behavior. Tool
   definitions, arguments, search/discovery, model reasoning and patches are not
   included. These read costs cannot establish end-to-end token savings.

An evidence-complete task means all its labeled lines were present. It does not
mean a coding task was solved. Required graph edges are checked separately;
their presence in the index does not establish that a response explained them.
For all six historical queries, the harness verifies that `auto` resolves to
`slices`, matching the audit's original detail choice.

## Recorded result

[baseline.json](baseline.json) records per-task ranks, returned paths and roles,
delivered ranges, exact rendered response token/byte counts and hashes, actual
oracle read costs, graph checks, source manifest identity and evaluated assembly
hashes. Machine-dependent timing is excluded. Generate all strata with
`python docs/eval/holdout/summarize.py`.

[initial.json](initial.json) preserves the first run before the follow-up C#
project-resolution correction. Baseline refreshes do not overwrite that file.

| Subset | Arm | Required files | Relevant lines | Evidence-complete tasks | Oracle follow-up reads |
| --- | --- | ---: | ---: | ---: | ---: |
| Historical 6 | Eligible candidates | 11/11 | — | — | — |
| Historical 6 | Unbudgeted top 8 | 9/11 | 97/191 | 0/6 | 8 |
| Historical 6 | JSON 2,000 | 7/11 | 78/191 | 0/6 | 9 |
| Historical 6 | Compact JSON 2,000 | 7/11 | 78/191 | 0/6 | 9 |
| Historical 6 | Markdown 2,000 | 7/11 | 95/191 | 0/6 | 8 |
| New 30 | Eligible candidates | 38/38 | — | — | — |
| New 30 | Unbudgeted top 8 | 38/38 | 260/449 | 13/30 | 21 |
| New 30 | JSON 2,000 | 29/38 | 204/449 | 9/30 | 26 |
| New 30 | Compact JSON 2,000 | 29/38 | 204/449 | 9/30 | 26 |
| New 30 | Markdown 2,000 | 32/38 | 205/449 | 10/30 | 25 |

The historical cost-model and reference-extractor files are both eligible at
rank 9, so their absence at top 8 is a selection problem. German
`FileScanner.cs` (rank 5) and `Receipt.cs` (rank 3) appear without the response
ceiling; their default JSON omissions are packing losses. Compact JSON has the
same aggregate file and line recall as default JSON on this frozen corpus.
For the same unbudgeted evidence across all 36 tasks, it uses 217,869 tokens
against default JSON's 246,297 (11.5% less). Cheaper serialization does not
guarantee broader retrieval: the greedy packer can spend freed space on richer
earlier items. Markdown delivers more required files on the new tasks, but
does not recover all required line ranges. The tight-budget 90% file-recall
target is not met: JSON delivers 29/38 (76.3%) required new-task occurrences.

The corrected graph contains all 11/11 labeled relationship occurrences,
including the four historical C# occurrences previously missing (the
scanner-to-matcher edge occurs in both language variants). Restoring those
relationships changes graph-based ranking. Compared with `initial.json`, new-task
unbudgeted recall improves from 37/38 to 38/38; JSON relevant-line coverage rises
from 183/449 to 204/449 and requires one fewer oracle full read (609 fewer body
tokens). Historical unbudgeted file recall falls from 10/11 to 9/11 and Markdown
from 9/11 to 7/11; the reference extractor moves outside top 8 and the English
ignore matcher no longer fits in Markdown. Historical default JSON stays 7/11.
These tradeoffs are retained in the initial/current reports; the baseline refresh
records a reviewed graph correction, not a universal relevance improvement.

## Packing experiment rejected by line coverage

After freezing and measuring the corpus, a two-pass packing prototype admitted
one relevant unit per file before adding more units to selected files. The
[experimental report](packing-experiment.json) and [exact experimental patch](
packing-experiment.patch) are preserved for reproduction; the patch is **not
part of the product** and the default packer was restored.

The prototype raised compact JSON new-task file recall from 29/38 to 36/38
(94.7%), but reduced relevant lines from 204/449 to 115/449 and evidence-complete
tasks from 9/30 to 3/30. Oracle full reads rose from 26 to 32 and their body tokens
from 43,184 to 53,734. Default JSON similarly gained two required files while
losing 105 relevant lines. It meets a file-only 90% goal by delivering less of
what each task needs, so it was rejected. Future packing changes must preserve
useful source evidence and follow-up cost as well as path recall.

## Reproduction and change control

```powershell
dotnet test tests/RepoContext.Integration.Tests/RepoContext.Integration.Tests.csproj -c Release --filter FullyQualifiedName~HoldoutEvaluationTests
python docs/eval/holdout/summarize.py
```

The tests reject changes to the frozen manifest/source bytes and require every
previously delivered required file and labeled source line to remain covered for
each task and surface. They also preserve previously indexed labeled graph edges,
reject response shortfalls and enforce exact response ceilings. Improvements may
pass without changing the snapshot; task-level regressions require review.

To deliberately publish a reviewed new measurement, set
`REPOCTX_UPDATE_HOLDOUT_BASELINE=1` for that test process. This updates only
`baseline.json`; it never changes labels or source archives. Review the diff and
refresh the table above using `summarize.py`. The initial authoring script
`freeze_manifest.py` is provenance for the fixed labels, not a test-baseline
update command. A new label set needs a new corpus identity and independent
review before tuning.
