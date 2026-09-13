# Review: read-cost guard and context lifetime

Reviewed branch: `docs/same-quality-lower-cost-plan`, PR #21.
Original implementation: `f409590b59d61e35acb69529997ef3f7941ba499`.
Release remains 0.15.0; these are corrections before its first release.

| Priority | Finding | Correction |
| --- | --- | --- |
| P1 | A missing/retired/evicted epoch ledger revived old receipt files, and reused epoch 1 could collide with an old conversation | Reserved generated namespace, fresh generation identities, invalidation on missing state, conservative child-context retirement |
| P1 | Denials were emitted even when repeat bookkeeping could not be persisted | Denial requires a successful bounded state write; lock/capacity/write failures allow normal handling |
| P1 | Substring ownership could replace or delete foreign hooks; updating a shared matcher changed foreign behavior | Recognize exact owned commands, preserve foreign groups and matchers, retain unowned empty configuration |
| P1 | Empty/partial comparisons could appear complete; subscription and invalid billing values were accepted as money | Validate matrix/provenance/quality/currency, exclude subscription billing, suppress unsupported comparative claims |
| P2 | The guarded evaluation arm installed observe mode, and workspaces/transcripts were discarded before rubric review | Explicit enforce setup and retained artifacts; setup/launch failures remain recorded |
| P2 | Benchmark claimed to be runnable but lacked patches, scenario/cache controllers and a usage adapter | Preflight reports implementation gaps and blocks the incomplete paid comparison; frozen tasks unchanged |
| P2 | Offset-only and near-EOF reads were priced as a whole file; same-size edits and changed config still used old metrics | Bound by remaining lines; reject changed configuration/newer metadata and symlink paths |
| P2 | Suggested outline paths failed from subdirectories; malformed settings returned successful check/install status | Absolute outline paths, integration failure exit status and regression tests |

Validation: the original CI was green. Nine new Python regression checks failed
on the original harness and passed after correction; the existing offline
self-check also passes. C# regression tests run in GitHub CI because this review
workspace has no .NET SDK and the SDK download host is unreachable. The PR's
latest commit checks are the authoritative build/platform result.

Two existing test assertions were updated for intentional corrected contracts:
malformed installation returns failure, and the announced session uses the v2
reserved prefix. No frozen retrieval goldens, task manifest or rubric were edited.

Remaining limitation: no real-agent quality/cost comparison has run. The frozen
benchmark still needs executable scenario fixtures and an actual usage adapter,
as well as credentials and pricing. Enforce remains experimental; there is no
measured savings or equal-quality claim and no merge/publication in this review.
