#!/usr/bin/env python3
"""Reproduce the functional audit with real CLI calls and dummy fixtures.

Run on a clean checkout of the commit being measured, after a Release build.
Uses only Python's standard library. Rebuilds that checkout's local index.
It measures evidence retrieval, not an LLM's ability to complete a coding task.
"""

import argparse
import json
import os
from pathlib import Path
import re
import sqlite3
import subprocess
import tempfile
import time


# Labels were set by source inspection before the first audit run. Keep them
# separate from rankings; do not change expectations merely to improve scores.
TASKS = [
    ("ignore-files-en", "exclude generated files and respect gitignore", [
        "src/RepoContext.Core/Scanning/FileScanner.cs",
        "src/RepoContext.Core/Scanning/GitignoreMatcher.cs",
    ]),
    ("ignore-files-de", "Generierte Dateien ausschliessen und Gitignore beachten", [
        "src/RepoContext.Core/Scanning/FileScanner.cs",
        "src/RepoContext.Core/Scanning/GitignoreMatcher.cs",
    ]),
    ("receipt-reuse", "avoid sending the same source spans twice using receipts", [
        "src/RepoContext.Core/Context/ContextEngine.cs",
        "src/RepoContext.Core/Identity/Receipt.cs",
    ]),
    ("response-budget", "fix response token budget packing", [
        "src/RepoContext.Core/Context/ContextEngine.cs",
        "src/RepoContext.Cli/Output/ContextCostModel.cs",
    ]),
    ("import-resolution", "resolve TypeScript module imports to source files", [
        "src/RepoContext.Core/Graph/GraphBuilder.cs",
        "src/RepoContext.Core/Graph/ReferenceExtractor.cs",
    ]),
    ("session-store", "persist and load agent session receipts", [
        "src/RepoContext.Core/Context/SessionStore.cs",
    ]),
]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True, type=Path,
                        help="Clean, Release-built checkout to index and query.")
    parser.add_argument("--output", required=True, type=Path,
                        help="New JSON result file, outside the audited checkout.")
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable.")
    args = parser.parse_args()
    repo = args.repo.resolve()
    output = args.output.resolve()
    cli = repo / "src/RepoContext.Cli/bin/Release/net10.0/repoctx.dll"
    if not cli.is_file():
        parser.error("Build the supplied checkout in Release configuration first.")
    if output == repo or repo in output.parents:
        parser.error("Place --output outside --repo so it cannot affect search results.")
    if output.exists():
        parser.error("Choose a new --output file to preserve earlier measurements.")

    def git(*argv):
        return subprocess.run(["git", *argv], cwd=repo, capture_output=True,
                              text=True, check=True).stdout.strip()

    if git("status", "--porcelain"):
        parser.error("Use a clean checkout; uncommitted files can change rankings.")
    results = {"source_commit": git("rev-parse", "HEAD"), "self_tasks": []}
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", REPOCTX_NO_STATS="1")
    env.pop("REPOCTX_SESSION", None)

    def run(root, *argv):
        start = time.perf_counter()
        process = subprocess.run([args.dotnet, str(cli), *argv], cwd=root,
                                 env=env, capture_output=True, text=True, timeout=90)
        result = {"args": list(argv), "exit": process.returncode,
                  "stdout": process.stdout, "stderr": process.stderr,
                  "seconds": round(time.perf_counter() - start, 3)}
        if process.returncode:
            raise RuntimeError(json.dumps(result, ensure_ascii=False))
        return result

    def write(root, path, text):
        file = root / path
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(text, encoding="utf-8")

    try:
        results["cli_version"] = run(repo, "--version")["stdout"].strip()
        with tempfile.TemporaryDirectory(prefix="repoctx-effectiveness-") as directory:
            temporary = Path(directory)

            def fixture(name):
                root = temporary / name
                root.mkdir()
                run(root, "init", "--no-agents")
                return root

            root = fixture("module-resolution")
            write(root, "tsconfig.json", json.dumps({
                "compilerOptions": {"paths": {"@/*": ["./src/*"]}}}))
            write(root, "src/session.ts",
                  'export function createSession() { return "OLD_SESSION"; }\n')
            for name, specifier in [("relative", "./session"),
                                    ("node-esm", "./session.js"),
                                    ("alias", "@/session")]:
                write(root, f"src/{name}.ts",
                      f'import {{ createSession }} from "{specifier}";\n'
                      'export const current = createSession();\n')
            run(root, "index")
            results["module_resolution"] = run(
                root, "related", "src/session.ts", "--format", "json")
            context = ("context", "createSession", "--path", "src/session.ts",
                       "--detail", "slices", "--format", "json")
            before = run(root, *context)
            write(root, "src/session.ts",
                  'export function createSession() { return "NEW_SESSION"; }\n')
            after = run(root, *context)
            results["stale_query"] = {
                "before": before, "after": after,
                "identical_response": before["stdout"] == after["stdout"],
                "changes": run(root, "changed", "--patch", "--format", "json"),
            }
            run(root, "index")
            results["stale_query"]["after_reindex"] = run(root, *context)

            root = fixture("sparse-config")
            write(root, "repoctx.config.json", "{}")
            write(root, "src/useful.ts", "export const UsefulMarker = 1;\n")
            write(root, "node_modules/demo/index.js",
                  "export const VendorNoiseMarker = 2;\n")
            write(root, ".env", "DUMMY_ONLY=SensitiveDummyMarker\n")
            write(root, "obj/generated.cs", "public class GeneratedNoiseMarker {}\n")
            write(root, ".git/description", "Dummy GitMetadataMarker\n")
            results["sparse_config_index"] = run(root, "index")
            with sqlite3.connect(root / ".repoctx/index.db") as database:
                results["sparse_config_files"] = [row[0] for row in
                    database.execute("SELECT path FROM files ORDER BY path")]

            root = fixture("csharp-duplicates")
            for folder, namespace in [("A", "Alpha"), ("B", "Beta")]:
                write(root, f"src/{folder}/User.cs",
                      f'namespace {namespace};\n'
                      f'public sealed class User {{ public string Name => "{folder}"; }}\n')
            run(root, "index")
            results["csharp_duplicate_types"] = run(
                root, "related", "src/A/User.cs", "--format", "json")
        print("Functional probes completed.", flush=True)

        results["self_index"] = run(repo, "index", "--full")
        results["self_noop_index"] = run(repo, "index")
        for identifier, query, expected in TASKS:
            response = run(repo, "context", query, "--detail", "auto", "--top", "8",
                           "--response-budget-tokens", "2000", "--format", "json")
            body = json.loads(response["stdout"])
            paths = [item["path"] for item in body["results"]]
            recall = sum(path in paths for path in expected) / len(expected)
            results["self_tasks"].append({
                "id": identifier, "query": query, "expected": expected,
                "returned": paths, "recall": recall, "response": response})
            print(f"{identifier}: recall={recall:.2f}, {response['seconds']} s", flush=True)

        query = "resolve TypeScript module imports to source files"
        common = ["context", query, "--detail", "auto", "--top", "8"]
        budget = ["--response-budget-tokens", "2000"]
        cases = [
            ("json-2000-isolated", common + budget + ["--format", "json"]),
            ("md-2000-isolated", common + budget + ["--format", "md"]),
            ("json-without-response-ceiling", common + ["--format", "json"]),
            ("json-2000-core-scope", common + budget + [
                "--path", "src/RepoContext.Core", "--format", "json"]),
            ("exact-symbol-search", ["search", "ResolveTsImport", "--symbols",
                                     "--format", "json"]),
        ]
        results["followups"] = []
        for identifier, argv in cases:
            result = run(repo, *argv)
            result["id"] = identifier
            if argv[-1] == "json":
                body = json.loads(result["stdout"])
                result["returned"] = [item["path"] for item in body["results"]]
                result["omitted_by"] = body.get("omitted_by")
            else:
                result["returned"] = re.findall(r"^### `([^`]+)`", result["stdout"], re.M)
            results["followups"].append(result)
            print(f"{identifier}: {result['seconds']} s", flush=True)
    except (RuntimeError, subprocess.TimeoutExpired) as error:
        results["incomplete"] = str(error)
        raise
    finally:
        output.parent.mkdir(parents=True, exist_ok=True)
        # Exclusive creation keeps previous evidence intact, including on failure.
        with output.open("x", encoding="utf-8") as stream:
            json.dump(results, stream, indent=2, ensure_ascii=False)
            stream.write("\n")


if __name__ == "__main__":
    main()
