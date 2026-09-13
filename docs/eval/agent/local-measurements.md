# Local measurements for the read-cost guard

These are **local, offline measurements of the guard itself**. They say how often
it would act on one repository and what it costs to run. They are not a saving
and not a quality result: only the paired three-arm comparison in
[`manifest.json`](manifest.json) can produce those, and it has not been executed
(see [README](README.md) for the missing credentials).

## Benchmark machine

| | |
| --- | --- |
| CPU | Intel Xeon @ 2.10 GHz, 4 vCPU (shared cloud VM) |
| Memory | 15 GiB |
| Kernel | Linux 6.18.44 |
| Runtime | .NET 10.0.401, self-contained `linux-x64` publish, `TrimTreeSitterGrammars=true` |
| Build | `repoctx` at the commit that introduced ADR 0023 |
| Repository under test | this repository, 409 indexed files, 1,125,619 raw `o200k_base` tokens |
| Date | 2026-09-12 |

A shared cloud VM is a pessimistic host: startup-dominated figures are worse here
than on a developer laptop. Treat the absolute numbers as an upper bound on this
class of machine and the *breakdown* as the transferable part.

## How often the guard would act

Share of this repository's indexed files whose whole-file read exceeds a
threshold, in raw `o200k_base` tokens (the shipped default is 2,000 in the active
calibration profile):

| Threshold | Files over it | Share of files | Tokens in those files | Share of token mass |
| ---: | ---: | ---: | ---: | ---: |
| 1,000 | 179 | 43.8 % | 1,039,168 | 92.3 % |
| 1,500 | 128 | 31.3 % | 976,355 | 86.7 % |
| **2,000** | **92** | **22.5 %** | **913,851** | **81.2 %** |
| 3,000 | 49 | 12.0 % | 812,638 | 72.2 % |
| 5,000 | 19 | 4.6 % | 702,836 | 62.4 % |

Read this as coverage, not as savings. It says that at the default threshold
roughly a fifth of the files carry four fifths of the token mass, which is why a
cost threshold is worth having at all. It does **not** say that those reads would
have happened, that the suggested alternative would have answered the task, or
that any tokens were avoided. A denied read is followed by another call, possibly
by a full read anyway, and possibly by recovery work — none of which this table
can see.

## Guard latency

Each figure is one complete `repoctx guard hook` process: spawn, read the payload
on stdin, decide, persist counters, exit. That is what a client actually waits
for.

| Scenario | n | p50 | p95 | max |
| --- | ---: | ---: | ---: | ---: |
| Expensive `Read` (index opened, counters written) | 60 | 196 ms | 225 ms | 227 ms |
| Unsupported shell command (no index opened) | 30 | 142 ms | 157 ms | 180 ms |
| First call after indexing (cold file cache) | 1 | 209 ms | — | — |

Where that time goes:

| Component | Cost |
| --- | ---: |
| `repoctx --version` (process start + CLI parse only) | 59 ms |
| Hook with no repository and no state write | 74 ms |
| `guard status` (state file + settings file read) | 108 ms |
| In-process evaluation as the guard's own counters record it (mean over 91 calls) | 91 ms |
| For comparison: `repoctx outline <file>` | 417 ms |

### The engineering target is not met on this machine

The plan set an initial target of **warm p95 below 100 ms**. Measured warm p95 is
**225 ms**, so the target is missed by more than a factor of two. It is not met
and is not declared met.

What the breakdown shows: about 74 ms is process startup and argument parsing
before any guard code runs, and that floor is paid by every invocation regardless
of what the guard decides. The identified next step is a ReadyToRun or
ahead-of-time compiled launcher, which changes the release pipeline and is
therefore out of scope for this change. Opening the index read-only and without
the schema pass was applied here; it is worth keeping because it stops a hook
from taking a write lock on an index an indexer may be writing, but it did not
measurably move the latency.

Consequences, stated rather than smoothed over:

* Enforce mode stays **experimental**. A policy that adds ~200 ms to inspected
  tool calls has to earn that back in measured spend, and nothing has measured it
  yet.
* The client-side `timeout` is 5 s and the guard's own budget is 400 ms, so an
  overrun degrades to allowing the read rather than to a stalled tool call.
* Observe mode carries the same latency as enforce. Installing it to gather
  coverage is not free.

## Reproducing

```sh
dotnet publish src/RepoContext.Cli/RepoContext.Cli.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:TrimTreeSitterGrammars=true -o artifacts/linux-x64

cd /path/to/a/checkout && repoctx init && repoctx index

# coverage
sqlite3 .repoctx/index.db \
  "select count(*), sum(token_count) from files where token_count > 2000"

# latency: time the complete hook process, not just the decision
printf '%s' "$PAYLOAD" | repoctx guard hook --mode observe
repoctx guard status       # the guard's own in-process counters
```

`repoctx guard check <file>` prints the decision and the estimated cost for one
path without writing any state.

## What is still unmeasured

* Every figure above is the guard in isolation. Task quality, total agent spend,
  follow-up reads, retries and compaction are **not** measured here.
* Token figures are estimates from stored `o200k_base` counts and the configured
  calibration profile, prorated by lines for partial reads. They are not billed
  tokens.
* One repository, one operating system, one machine. Cross-platform latency and
  behaviour on very large repositories are untested.
* Whether a redirected read leads to an equally good answer is exactly the
  question the three-arm comparison exists to answer, and it is open.
