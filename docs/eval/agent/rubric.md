# Frozen quality rubric

Frozen together with [`manifest.json`](manifest.json) on 2026-09-12, before any
arm was run and before the read-cost guard was tuned. Changing a definition here
requires a new manifest id; it must never be edited to make a result look better.

Scoring is per run, by an adjudicator who does not know which arm produced the
output. Arm identity is stripped from the transcript before scoring.

## 1. Acceptance (binary, per task)

A run is **accepted** only when every acceptance check of the task passes:

* the task's `repo_tests` command exits 0,
* every `assert_*` check holds,
* the change is inside the scope the prompt asked for.

Timeouts, crashes and abandoned runs are **not accepted** and stay in the
denominator and in the cost total.

## 2. Rubric dimensions (0-3 each)

Only the dimensions listed for a task are scored.

| Dimension | 0 | 1 | 2 | 3 |
| --- | --- | --- | --- | --- |
| `correctness` | Wrong or broken | Works on the example only | Correct, gaps at edges | Correct including edges |
| `scope_discipline` | Unrelated files rewritten | Noticeable extra changes | Small unrequested edits | Exactly the requested change |
| `convention_fit` | Fights the codebase | Foreign but working | Mostly idiomatic | Indistinguishable from surrounding code |
| `evidence_use` | Asserts without looking | Reads far more than needed | Reasonable reads | Targeted evidence, nothing wasted |
| `explanation` | Missing or wrong | Vague | Clear, some gaps | Clear, complete, short |
| `accuracy` | Wrong claims | Partly wrong | Correct, incomplete | Correct and complete |
| `citation_validity` | No or invented citations | Some citations do not resolve | All resolve, some imprecise | All resolve to the exact range |
| `completeness` | Misses the question | Answers a part | Answers, omits an aspect | Answers fully |
| `brevity` | Padded | Long-winded | Slightly long | As short as the content allows |
| `defect_recall` | Finds nothing | Finds minor items only | Finds most seeded defects | Finds every seeded defect |
| `false_positive_rate` | Mostly noise | More noise than findings | One or two spurious findings | No spurious findings |

A citation "resolves" when the named file exists at the run's commit and the
named line range contains the cited construct. A citation to a file the agent
never received evidence for is an invented citation, scored 0 on
`citation_validity`, whatever the prose says.

## 3. Defect severity (worst defect in the run)

| Severity | Definition |
| --- | --- |
| `none` | No defect found by adjudication or by the repository's tests. |
| `minor` | Cosmetic or stylistic; no behaviour change needed. |
| `major` | Wrong behaviour in a case the task named, or a missing test the task asked for. |
| `severe` | Silent data/evidence loss, a security or privacy regression, a wrong claim of possession of evidence that was never delivered, or a change that breaks unrelated behaviour. |

A **severe** defect fails the run regardless of acceptance checks.

## 4. Human repair

`human_repair_minutes` is measured with a clock by the adjudicator, doing the
smallest change that makes the run acceptable. It is never estimated from the
diff size. A run that is accepted with no adjudicator change records 0.

## 5. What this rubric cannot do

A finite set of tasks and five repetitions cannot establish equal quality on
unseen work. The rubric produces paired per-task observations; the conclusion
they support is an interval, not an equality. Any published claim must carry the
workload, the repetition count and the interval it came from.
