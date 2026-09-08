#!/usr/bin/env python3
"""Repeated, sequential CLI measurements; no models or network calls.

Build the candidate in Release first. Run without concurrent builds/tests. By
default this generates 300/3,000/30,000-file C#/TS repositories and checks exact
stdout budgets. --repo optionally adds the six frozen original audit tasks on
a clean source checkout. Results are exclusively created outside that checkout.
Only Python's standard library and the installed .NET SDK are required.
"""

import argparse
from contextlib import closing
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import sqlite3
import statistics
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone
from xml.sax.saxutils import escape

sys.dont_write_bytecode = True
from functional_effectiveness_probe import TASKS


def positive(value):
    number = int(value)
    if number < 1:
        raise argparse.ArgumentTypeError("must be positive")
    return number


def distribution(samples):
    """p95 is the nearest-rank order statistic, not an interpolated estimate."""
    ordered = sorted(samples)
    return {"n": len(ordered), "median": statistics.median(ordered),
            "p95": ordered[math.ceil(0.95 * len(ordered)) - 1],
            "min": ordered[0], "max": ordered[-1]}


def write(root, name, content):
    path = root / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8", newline="\n")


def generate(root, count):
    """Dense lexical matches stress packing; these are not real coding tasks."""
    write(root, "repoctx.config.json", json.dumps({"include": ["src"]}))
    expected = ["src/envelope/ResponseBudget.cs", "src/envelope/responseBudget.ts"]
    write(root, expected[0], """namespace Scale.Target;
public static class ResponseBudget
{
    // Fix response token budget packing, including the final response envelope.
    public static bool FitsResponseTokenBudget(int responseTokens, int budget)
    {
        var finalEnvelopeTokens = responseTokens + 12;
        return finalEnvelopeTokens <= budget;
    }
}
""")
    write(root, expected[1], """// Fix response token budget packing for the serialized response.
export function fitsResponseTokenBudget(responseTokens: number, budget: number) {
    const finalEnvelopeTokens = responseTokens + 12;
    return finalEnvelopeTokens <= budget;
}
""")
    for index in range(count - 2):
        shard = index // 100
        if index % 2:
            write(root, f"src/shard{shard:04}/BudgetPacking{index:05}.cs", f"""namespace Scale.Shard{shard};
public static class BudgetPacking{index:05}
{{
    // Pack a response using a token budget; retain a deterministic shard marker.
    public static int PackResponse(int tokens, int budget)
    {{
        var marker = {index};
        return tokens <= budget ? marker : -1;
    }}
}}
""")
        else:
            write(root, f"src/shard{shard:04}/budgetPacking{index:05}.ts", f"""// Pack a response using a token budget; retain a deterministic shard marker.
export function packResponse{index:05}(tokens: number, budget: number): number {{
    const marker = {index};
    return tokens <= budget ? marker : -1;
}}
""")
    return [("dense-budget-packing", "fix response token budget packing", expected)]


def build_counter(directory, cli, dotnet, env):
    """Use the candidate's local BPE vocabulary, outside its budget machinery."""
    refs = []
    for name in ("RepoContext.Core", "Microsoft.ML.Tokenizers",
                 "Microsoft.ML.Tokenizers.Data.O200kBase"):
        dependency = cli.parent / (name + ".dll")
        if not dependency.is_file():
            raise RuntimeError(f"Missing tokenizer dependency: {dependency}")
        refs.append(f'<Reference Include="{name}"><HintPath>{escape(str(dependency))}'
                    '</HintPath></Reference>')
    write(directory, "Counter.csproj", """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>""" + "".join(refs) + "</ItemGroup></Project>\n")
    write(directory, "Program.cs", """using System.Text.Json;
using RepoContext.Core.Indexing;
string? line;
while ((line = Console.ReadLine()) is not null)
{
    Console.WriteLine(Tokens.Count(JsonSerializer.Deserialize<string>(line)!));
}
""")
    write(directory, "NuGet.Config", "<configuration><packageSources><clear /></packageSources></configuration>\n")
    process = subprocess.run([dotnet, "build", str(directory / "Counter.csproj"),
                              "-c", "Release", "--nologo",
                              f"-p:RestoreConfigFile={directory / 'NuGet.Config'}",
                              "-p:NuGetAudit=false"],
                             env=env, capture_output=True, timeout=180)
    if process.returncode:
        raise RuntimeError(process.stdout.decode("utf-8", errors="replace")
                           + process.stderr.decode("utf-8", errors="replace"))
    return directory / "bin/Release/net10.0/Counter.dll"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True, help="Built candidate repoctx.dll.")
    parser.add_argument("--output", type=Path, required=True, help="New JSON evidence file.")
    parser.add_argument("--repo", type=Path, help="Optional clean checkout for frozen audit tasks.")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--scales", type=positive, nargs="*", default=[300, 3000, 30000],
                        help="Generated source-file counts; no values skips synthetic repositories.")
    parser.add_argument("--repetitions", type=positive, default=5)
    parser.add_argument("--warmups", type=int, default=1)
    parser.add_argument("--budgets", type=positive, nargs="+", default=[1000, 2000])
    parser.add_argument("--formats", choices=["json", "md"], nargs="+", default=["json", "md"])
    parser.add_argument("--compact", action="store_true", help="Also measure --compact JSON.")
    parser.add_argument("--skip-candidate-control", action="store_true",
                        help="Skip exhaustive paths controls on synthetic scales; report candidate recall only if all labels appear at top 8.")
    parser.add_argument("--timeout", type=positive, default=300, help="Per-process timeout in seconds.")
    args = parser.parse_args()
    cli, output = args.cli.resolve(), args.output.resolve()
    repo = args.repo.resolve() if args.repo else None
    if not cli.is_file():
        parser.error("--cli must name an already built Release CLI DLL")
    if args.warmups < 0 or any(scale < 2 for scale in args.scales):
        parser.error("warmups must be nonnegative; scales must contain at least two files")
    if not args.scales and repo is None:
        parser.error("supply --repo or at least one scale")
    if output.exists():
        parser.error("choose a new output path; previous evidence is never overwritten")
    if repo is not None and (output == repo or repo in output.parents):
        parser.error("--output must be outside --repo to avoid changing its evidence")
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
               REPOCTX_NO_STATS="1", PYTHONDONTWRITEBYTECODE="1")
    env.pop("REPOCTX_SESSION", None)
    results = {
        "schema_version": 1,
        "started_utc": datetime.now(timezone.utc).isoformat(),
        "platform": platform.platform(), "processor": platform.processor(),
        "logical_cpus": os.cpu_count(), "python": platform.python_version(),
        "cli_sha256": hashlib.sha256(cli.read_bytes()).hexdigest(),
        "core_sha256": hashlib.sha256((cli.parent / "RepoContext.Core.dll").read_bytes()).hexdigest(),
        "harness_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "settings": {"scales": args.scales, "repetitions": args.repetitions,
                     "warmups": args.warmups, "budgets": args.budgets,
                     "formats": args.formats, "compact": args.compact,
                     "skip_synthetic_candidate_control": args.skip_candidate_control},
        "method": {
            "timing": "Sequential new CLI processes on a warmed index/filesystem; includes process/CLR startup.",
            "isolation": "No parallel calls within harness. Operator must stop unrelated builds/tests.",
            "p95": "Nearest-rank p95; at fewer than 20 samples this is the sample maximum.",
            "tokens": "Exact unnormalized UTF-8 stdout, including trailing newline, recounted after timings with local o200k_base.",
            "candidate_recall": "Observable eligible paths from unbudgeted paths detail with top=indexed-file-count; not raw internal search hits.",
            "quality": "Static expected-file retrieval only. No agent coding-task success or model-session cost is measured.",
        },
        "repositories": [], "checks": [],
    }
    recount = []

    def run(root, argv, budget=None, allow_budget_error=False):
        started = time.perf_counter()
        try:
            process = subprocess.run([args.dotnet, str(cli), *argv], cwd=root,
                                     env=env, capture_output=True, timeout=args.timeout)
        except subprocess.TimeoutExpired as error:
            results["failed_call"] = {
                "args": argv, "timeout_seconds": args.timeout,
                "seconds": time.perf_counter() - started,
                "stdout": (error.stdout or b"").decode("utf-8", errors="replace"),
                "stderr": (error.stderr or b"").decode("utf-8", errors="replace"),
            }
            raise
        elapsed = time.perf_counter() - started
        # bytes capture avoids Python's universal-newline conversion on Windows.
        record = {"args": argv, "exit": process.returncode, "seconds": elapsed,
                  "stdout": process.stdout.decode("utf-8"),
                  "stderr": process.stderr.decode("utf-8"), "budget": budget}
        if argv[0] == "context":
            recount.append(record)
        if process.returncode:
            if not allow_budget_error or "retry_budget_tokens" not in record["stderr"] + record["stdout"]:
                results["failed_call"] = record
                raise RuntimeError(json.dumps(record, ensure_ascii=False))
        return record

    def paths(record, format_name="json"):
        if record["exit"]:
            return []
        if format_name == "md":
            import re
            return re.findall(r"^### `([^`]+)`", record["stdout"], re.M)
        return [item["path"] for item in json.loads(record["stdout"])["results"]]

    def recall(expected, actual):
        return sum(path in actual for path in expected) / len(expected)

    def measure(root, identifier, tasks, requested_files=None):
        entry = {"id": identifier, "tasks": []}
        results["repositories"].append(entry)
        entry["cold_index"] = run(root, ["index", "--full"])
        entry["noop_index"] = run(root, ["index"])
        with closing(sqlite3.connect(root / ".repoctx/index.db")) as database:
            entry["indexed_files"] = database.execute("SELECT COUNT(*) FROM files").fetchone()[0]
        if requested_files is not None and entry["indexed_files"] != requested_files:
            raise RuntimeError(f"Expected {requested_files} indexed files, got {entry['indexed_files']}")
        for task_id, query, expected in tasks:
            task = {"id": task_id, "query": query, "expected": expected, "cases": []}
            entry["tasks"].append(task)
            candidates = None
            if requested_files is not None and args.skip_candidate_control:
                task["candidate_control"] = None
                task["candidate_control_complete"] = False
                task["candidate_control_note"] = "Skipped exhaustive synthetic paths control; no candidate list/count measured."
            else:
                task["candidate_control"] = run(root, ["context", query, "--no-memory",
                    "--detail", "paths", "--top", str(entry["indexed_files"]), "--format", "json"])
                candidate_body = json.loads(task["candidate_control"]["stdout"])
                candidates = paths(task["candidate_control"])
                task["eligible_candidate_recall"] = recall(expected, candidates)
                task["eligible_candidate_count"] = len(candidates)
                task["candidate_control_omitted_by"] = candidate_body.get("omitted_by", {})
                # Nonpositive-score exclusions are outside the eligible set.
                task["candidate_control_complete"] = not any(
                    candidate_body.get("omitted_by", {}).get(key, 0)
                    for key in ("top", "response_budget", "budget_tokens", "projected_read_budget"))
            common = ["context", query, "--no-memory", "--detail", "auto", "--top", "8"]
            variants = [("json-unbounded", "json", None, [])]
            for budget in args.budgets:
                for format_name in args.formats:
                    variants.append((f"{format_name}-{budget}", format_name, budget, []))
                if args.compact:
                    variants.append((f"json-compact-{budget}", "json", budget, ["--compact"]))
            # Warm every case before measuring; rotate order across repetitions
            # to avoid assigning all late-run drift to the last representation.
            for case_id, format_name, budget, extra in variants:
                argv = common + ["--format", format_name] + extra
                if budget is not None:
                    argv += ["--response-budget-tokens", str(budget)]
                case = {"id": case_id, "format": format_name, "budget": budget,
                        "args": argv, "warmups": [], "samples": []}
                task["cases"].append(case)
                for _ in range(args.warmups):
                    case["warmups"].append(run(root, argv, budget, True))
            for repetition in range(args.repetitions):
                order = task["cases"][repetition % len(variants):] + task["cases"][:repetition % len(variants)]
                for case in order:
                    sample = run(root, case["args"], case["budget"], True)
                    sample["repetition"] = repetition + 1
                    sample["returned"] = paths(sample, case["format"])
                    sample["expected_recall"] = recall(expected, sample["returned"])
                    case["samples"].append(sample)
                    print(f"{identifier}/{task_id}/{case['id']} #{repetition + 1}: "
                          f"{sample['seconds']:.3f}s, recall={sample['expected_recall']:.2f}", flush=True)
            unbounded = task["cases"][0]["samples"][0]["returned"]
            task["unbounded_top8_recall"] = recall(expected, unbounded)
            if candidates is None:
                # Output can prove a labelled file was a candidate, but absence
                # from top 8 cannot prove absence from the complete candidate set.
                task["eligible_candidate_recall"] = 1.0 if task["unbounded_top8_recall"] == 1.0 else None
                task["eligible_candidate_recall_lower_bound"] = task["unbounded_top8_recall"]
                task["eligible_candidate_count"] = None
                task["missing_from_candidates"] = None
                task["candidate_present_but_missing_at_top8"] = None
            else:
                task["missing_from_candidates"] = [path for path in expected if path not in candidates]
                task["candidate_present_but_missing_at_top8"] = [
                    path for path in expected if path in candidates and path not in unbounded]
            for case in task["cases"]:
                case["latency_seconds"] = distribution([sample["seconds"] for sample in case["samples"]])
                case["deterministic_stdout"] = len({sample["stdout"] for sample in case["samples"]}) == 1
                case["successful_samples"] = sum(sample["exit"] == 0 for sample in case["samples"])
                case["budget_shortfalls"] = len(case["samples"]) - case["successful_samples"]
                case["unbounded_present_but_missing_when_budgeted"] = [
                    path for path in expected if path in unbounded
                    and path not in case["samples"][0]["returned"]]

    try:
        results["dotnet_info"] = subprocess.run([args.dotnet, "--info"], env=env,
            capture_output=True, check=True, timeout=30).stdout.decode("utf-8")
        with tempfile.TemporaryDirectory(prefix="repoctx-scaling-") as temporary:
            temp = Path(temporary)
            counter = build_counter(temp / "counter", cli, args.dotnet, env)
            results["cli_version"] = run(temp, ["--version"])["stdout"].strip()
            if repo is not None:
                status = subprocess.run(["git", "status", "--porcelain"], cwd=repo,
                    capture_output=True, check=True, timeout=30).stdout
                if status.strip():
                    raise RuntimeError("--repo must be clean; uncommitted files change rankings")
                results["source_commit"] = subprocess.run(["git", "rev-parse", "HEAD"],
                    cwd=repo, capture_output=True, check=True, timeout=30).stdout.decode().strip()
                measure(repo, "frozen-self-repository", TASKS)
            for count in args.scales:
                root = temp / f"scale-{count}"
                tasks = generate(root, count)
                measure(root, f"synthetic-{count}", tasks, count)
            token_process = subprocess.run([args.dotnet, str(counter)], env=env,
                input="".join(json.dumps(record["stdout"], ensure_ascii=True) + "\n"
                              for record in recount).encode("utf-8"),
                capture_output=True, check=True, timeout=max(120, args.timeout))
            counts = [int(value) for value in token_process.stdout.decode("utf-8").splitlines()]
            if len(counts) != len(recount):
                raise RuntimeError("Tokenizer did not return one count for every captured response")
            for record, count in zip(recount, counts):
                record["exact_stdout_tokens"] = count
                if record["budget"] is not None:
                    record["budget_ok"] = count <= record["budget"]
                    results["checks"].append(record["budget_ok"])
            for entry in results["repositories"]:
                for task in entry["tasks"]:
                    for case in task["cases"]:
                        case["stdout_tokens"] = distribution([
                            sample["exact_stdout_tokens"] for sample in case["samples"]])
                        results["checks"].append(case["deterministic_stdout"])
            results["all_checks_passed"] = all(results["checks"])
            if (hashlib.sha256(cli.read_bytes()).hexdigest() != results["cli_sha256"]
                    or hashlib.sha256((cli.parent / "RepoContext.Core.dll").read_bytes()).hexdigest()
                    != results["core_sha256"]):
                raise RuntimeError("Candidate binaries changed during measurement; rerun after builds stop")
            if not results["all_checks_passed"]:
                raise RuntimeError("Exact stdout budget or deterministic-response check failed; inspect raw samples")
    except Exception as error:
        results["all_checks_passed"] = False
        results["incomplete"] = str(error)
        raise
    finally:
        results["finished_utc"] = datetime.now(timezone.utc).isoformat()
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open("x", encoding="utf-8") as stream:
            json.dump(results, stream, ensure_ascii=False, indent=2)
            stream.write("\n")


if __name__ == "__main__":
    main()
