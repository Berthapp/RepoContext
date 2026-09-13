#!/usr/bin/env python3
"""Paired three-arm agent comparison runner (milestone 1 of the same-quality,
lower-cost plan).

This harness is opt-in and deliberately separate from the offline test suite: it
starts a real coding agent and therefore costs real money. Nothing in normal
RepoContext operation calls it.

    python3 run_comparison.py --check                 # prerequisites only
    python3 run_comparison.py --config agent.json     # the real comparison
    python3 run_comparison.py --stub-agent            # offline harness self-check

The runner writes one JSON object per run to ``runs.jsonl`` and never aggregates:
scoring lives in ``score_comparison.py`` so a scoring change can be re-run over
recorded runs without paying for the agent again.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import zipfile

HERE = pathlib.Path(__file__).resolve().parent
MANIFEST = HERE.parent / "manifest.json"

# Every field the plan requires per run. A run record missing any of these is
# incomplete and is reported as such rather than silently averaged away.
REQUIRED_PROVENANCE = (
    "task_id",
    "arm_id",
    "run_id",
    "repetition",
    "cache_mode",
    "repo_commit",
    "repoctx_version",
    "agent_client_version",
    "model_id",
    "pricing_source",
    "pricing_date",
    "pricing_currency",
)

SPEND_FIELDS = (
    "uncached_input_tokens",
    "cache_read_input_tokens",
    "cache_write_input_tokens",
    "output_tokens",
    "reasoning_tokens_billed_separately",
)


class PrerequisiteError(RuntimeError):
    """A prerequisite for a real (non-stub) comparison run is missing."""


def load_manifest(path: pathlib.Path = MANIFEST) -> dict:
    data = json.loads(path.read_text(encoding="utf-8"))
    digest = hashlib.sha256(path.read_text(encoding="utf-8").encode()).hexdigest()
    data["_manifest_sha256"] = digest
    return data


def manifest_repositories(manifest: dict) -> dict:
    return {repo["id"]: repo for repo in manifest["repositories"]}


# --------------------------------------------------------------------------- #
# prerequisites
# --------------------------------------------------------------------------- #


def check_prerequisites(manifest: dict, config: dict | None) -> list[str]:
    """Returns the concrete list of what is missing for a real run.

    An empty list means the comparison can be executed here. The list is
    deliberately concrete: "no agent credential" is the honest reason a cost
    claim cannot be made, and it belongs in the report, not in a footnote.
    """
    missing: list[str] = []

    if config is None:
        missing.append(
            "no --config: the runner needs an agent invocation description "
            "(see agent.example.json)"
        )
    else:
        if config.get("usage_adapter_contract") != 1:
            missing.append("a usage adapter implementing REPOCTX_EVAL_USAGE is required; "
                           "the raw claude CLI does not write that file")
        for field in ("agent_client_version", "model_id", "baseline_repoctx_version", "repoctx_version"):
            value = config.get(field)
            if not isinstance(value, str) or not value or "fill in" in value:
                missing.append(f"config.{field}: exact pinned value required")
        for field in ("repoctx", "baseline_repoctx"):
            binary = config.get(field)
            if not binary or not pathlib.Path(binary).is_absolute() or not pathlib.Path(binary).is_file():
                missing.append(f"config.{field}: absolute path to a separate pinned repoctx executable")
        for field in ("date", "currency"):
            value = config.get("pricing", {}).get(field)
            if not value or "YYYY" in value:
                missing.append(f"config.pricing.{field}: real value required")
        if config.get("command") and shutil.which(config["command"][0]) is None:
            missing.append("config.command: agent usage adapter executable is unavailable")
        if not config.get("command"):
            missing.append("config.command: argv template that starts the agent")
        for name in config.get("required_env", []):
            if not os.environ.get(name):
                missing.append(f"environment variable {name} is unset (agent credential)")
        if not config.get("pricing", {}).get("source"):
            missing.append(
                "config.pricing.source: provider pricing sheet identifier and date; "
                "without it spend cannot be expressed as money"
            )

    # The frozen manifest names these scenarios but does not supply their
    # patches, controllers or adjudication assets. Credentials cannot fill that
    # implementation gap. Block paid runs until a new, executable manifest exists.
    for task in manifest["tasks"]:
        if task["kind"] == "review":
            missing.append(f"{task['id']}: review patch and seeded-defect fixture are not implemented")
        for scenario in ("compaction", "stale_index"):
            if scenario in task.get("scenario", []):
                missing.append(f"{task['id']}: {scenario} scenario controller is not implemented")
    missing.append("cold/warm cache scenario control is not implemented; repetition number is not cache evidence")

    for repo in manifest["repositories"]:
        archive = (MANIFEST.parent / repo["archive"]).resolve()
        if not archive.is_file():
            missing.append(f"missing source archive {repo['archive']}")
            continue
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        if digest != repo["archive_sha256"]:
            missing.append(
                f"source archive {repo['archive']} has sha256 {digest}, "
                f"manifest freezes {repo['archive_sha256']}"
            )
        for tool in repo.get("prerequisites", []):
            root = tool.split()[0].split("-")[0]
            if root in ("node", "npm") and shutil.which(root) is None:
                missing.append(f"{repo['id']}: {root} is not installed ({tool})")
            if root == "dotnet" and shutil.which("dotnet") is None:
                missing.append(f"{repo['id']}: the .NET SDK is not installed ({tool})")

    if shutil.which("git") is None:
        missing.append("git is not installed; isolated worktrees cannot be created")

    return sorted(set(missing))


# --------------------------------------------------------------------------- #
# run plan
# --------------------------------------------------------------------------- #


def plan_runs(manifest: dict, repetitions: int | None = None) -> list[dict]:
    """The counterbalanced, paired execution plan.

    Arm order rotates with the repetition index, so an arm can never sit at a
    fixed position relative to warm caches or machine state.
    """
    arms = [arm["id"] for arm in manifest["arms"]]
    total = repetitions if repetitions is not None else manifest["protocol"]["repetitions_per_task_and_arm"]
    if total <= 0:
        raise ValueError("repetitions must be positive")
    runs: list[dict] = []
    for task in manifest["tasks"]:
        for repetition in range(1, total + 1):
            rotation = (repetition - 1) % len(arms)
            for position, arm in enumerate(arms[rotation:] + arms[:rotation]):
                runs.append(
                    {
                        "task_id": task["id"],
                        "arm_id": arm,
                        "repetition": repetition,
                        "arm_position": position,
                        "cache_mode": "cold" if repetition == 1 else task["cache_mode"],
                        "run_id": f"{task['id']}::{arm}::r{repetition}",
                    }
                )
    return runs


# --------------------------------------------------------------------------- #
# workspace
# --------------------------------------------------------------------------- #


def materialize(repo: dict, destination: pathlib.Path) -> None:
    """Extracts a pinned source snapshot into a fresh directory."""
    archive = (MANIFEST.parent / repo["archive"]).resolve()
    destination.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as zf:
        zf.extractall(destination)


def arm_setup_commands(arm: dict, repoctx: str) -> list[list[str]]:
    steps = {
        "remove-repoctx-integration": [[repoctx, "integrate", "--remove"]],
        "repoctx-init": [[repoctx, "init"]],
        "repoctx-index": [[repoctx, "index"]],
        "repoctx-integrate": [[repoctx, "integrate", "--client", "claude-code"]],
        "repoctx-integrate-guard": [
            [repoctx, "integrate", "--client", "claude-code", "--guard", "--guard-mode", "enforce"]
        ],
    }
    commands: list[list[str]] = []
    for step in arm.get("setup", []):
        if step not in steps:
            raise ValueError(f"unimplemented setup step: {step}")
        commands.extend(steps[step])
    return commands


# --------------------------------------------------------------------------- #
# execution
# --------------------------------------------------------------------------- #


def run_one(
    run: dict,
    task: dict,
    arm: dict,
    repo: dict,
    config: dict,
    workspace: pathlib.Path,
) -> dict:
    """Executes one run and returns its record.

    The agent command is given the prompt on stdin and is expected to write a
    JSON usage report to the path in ``REPOCTX_EVAL_USAGE``. Whatever it fails to
    report stays absent: this harness never invents a usage number.
    """
    materialize(repo, workspace)
    repoctx = config.get("baseline_repoctx" if arm["id"] == "current" else "repoctx", "repoctx")
    env = dict(os.environ)
    env.update(config.get("env", {}))
    env.pop("REPOCTX_SESSION", None)
    if pathlib.Path(repoctx).is_absolute():
        env["PATH"] = str(pathlib.Path(repoctx).parent) + os.pathsep + env.get("PATH", "")
    setup_failures: list[str] = []
    setup_started = _dt.datetime.now(_dt.timezone.utc)
    for command in arm_setup_commands(arm, repoctx):
        if command == [repoctx, "init"] and (workspace / "repoctx.config.json").is_file():
            continue
        try:
            completed_setup = subprocess.run(
                command, cwd=workspace, check=False, capture_output=True, env=env,
                timeout=config.get("setup_timeout_seconds", 120)
            )
            if completed_setup.returncode != 0:
                setup_failures.append(f"{' '.join(command)}: exit {completed_setup.returncode}")
        except subprocess.TimeoutExpired:
            setup_failures.append("setup command timed out")
        except OSError as error:
            # A missing launcher is a recorded fact about the run, not a reason
            # to lose the rest of the batch.
            setup_failures.append(f"{' '.join(command)}: {error.strerror}")

    usage_path = workspace / ".eval-usage.json"
    env["REPOCTX_EVAL_USAGE"] = str(usage_path)
    env["REPOCTX_EVAL_ARM"] = run["arm_id"]
    env["REPOCTX_EVAL_TASK"] = run["task_id"]

    started = _dt.datetime.now(_dt.timezone.utc)
    timeout = config.get("timeout_seconds", 1800)
    timed_out = False
    agent_error = None
    stderr = ""
    try:
        if setup_failures and config.get("billing_mode") != "stub":
            raise OSError("setup failed; paid agent was not started")
        completed = subprocess.run(
            config["command"],
            cwd=workspace,
            input=task["prompt"],
            text=True,
            capture_output=True,
            env=env,
            timeout=timeout,
        )
        exit_code = completed.returncode
        transcript = completed.stdout
        stderr = completed.stderr
    except OSError as error:
        agent_error = str(error)
        exit_code = None
        transcript = ""
    except subprocess.TimeoutExpired as error:
        timed_out = True
        exit_code = None
        transcript = error.stdout or ""
        stderr = error.stderr or ""
    if isinstance(transcript, bytes):
        transcript = transcript.decode("utf-8", errors="replace")
    if isinstance(stderr, bytes):
        stderr = stderr.decode("utf-8", errors="replace")
    (workspace / ".eval-transcript.txt").write_text(transcript, encoding="utf-8")
    (workspace / ".eval-stderr.txt").write_text(stderr, encoding="utf-8")
    finished = _dt.datetime.now(_dt.timezone.utc)

    usage: dict = {}
    if usage_path.is_file():
        try:
            parsed_usage = json.loads(usage_path.read_text(encoding="utf-8"))
            usage = parsed_usage if isinstance(parsed_usage, dict) else {}
        except json.JSONDecodeError:
            usage = {}

    record = {
        "schema_version": 1,
        "task_id": run["task_id"],
        "arm_id": run["arm_id"],
        "run_id": run["run_id"],
        "repetition": run["repetition"],
        "arm_position": run["arm_position"],
        "cache_mode": run["cache_mode"],
        "cache_mode_verified": False,
        "config_hash": hashlib.sha256(json.dumps(config, sort_keys=True).encode()).hexdigest(),
        "guard_mode": "enforce" if arm["id"] == "guarded" else "off",
        "artifact_directory": str(workspace.resolve()),
        "setup_seconds": round((started - setup_started).total_seconds(), 3),
        "agent_error": agent_error,
        "started_utc": started.isoformat(),
        "wall_clock_seconds": round((finished - started).total_seconds(), 3),
        "timed_out": timed_out,
        "agent_exit_code": exit_code,
        "repo_commit": repo["commit"],
        "repoctx_version": config.get(
            "baseline_repoctx_version" if arm["id"] == "current" else "repoctx_version"),
        "agent_client_version": config.get("agent_client_version"),
        "model_id": config.get("model_id"),
        "pricing_source": config.get("pricing", {}).get("source"),
        "pricing_date": config.get("pricing", {}).get("date"),
        "pricing_currency": config.get("pricing", {}).get("currency"),
        "spend": {field: usage.get(field) for field in SPEND_FIELDS},
        "billed_amount": usage.get("billed_amount"),
        "billing_mode": usage.get("billing_mode", config.get("billing_mode", "api")),
        "quota_consumed": usage.get("quota_consumed"),
        "workflow": usage.get("workflow", {}),
        "hook_latency_ms": usage.get("hook_latency_ms", {}),
        "guard": usage.get("guard", {}),
        "transcript_sha256": hashlib.sha256(transcript.encode("utf-8")).hexdigest(),
        "setup_failures": setup_failures,
    }
    record["usage_complete"] = all(
        record["spend"].get(field) is not None
        for field in SPEND_FIELDS
        if field != "reasoning_tokens_billed_separately"
    )
    record["quality"] = evaluate_acceptance(
        task, workspace, timed_out, run_checks=not config.get("skip_acceptance_commands", False)
    )
    if setup_failures or agent_error or exit_code not in (0,):
        record["quality"]["accepted"] = False
    return record


def evaluate_acceptance(
    task: dict, workspace: pathlib.Path, timed_out: bool, run_checks: bool = True
) -> dict:
    """Runs the task's machine-checkable acceptance checks.

    Rubric dimensions and defect severity are adjudicated by a human against
    ``rubric.md`` and merged in later; they are recorded as ``null`` here rather
    than guessed, so an unscored run can never be counted as a good one.
    """
    checks: list[dict] = []
    accepted: bool | None = False if timed_out else None
    for check in task["acceptance"]:
        if check["type"] == "repo_tests" and not timed_out and run_checks:
            try:
                result = subprocess.run(
                    check["command"], cwd=workspace, shell=True, capture_output=True,
                    text=True, timeout=1800,
                )
                checks.append({"type": "repo_tests", "passed": result.returncode == 0})
                (workspace / f".eval-check-{len(checks)}.txt").write_text(
                    result.stdout + result.stderr, encoding="utf-8")
            except (OSError, subprocess.TimeoutExpired):
                checks.append({"type": "repo_tests", "passed": False, "error": "check failed or timed out"})
        elif check["type"] == "rubric_only":
            checks.append({"type": "rubric_only", "passed": None})
        else:
            checks.append({"type": check["type"], "passed": None})

    decided = [c["passed"] for c in checks if c["passed"] is not None]
    if timed_out:
        accepted = False
    elif decided and all(decided) and len(decided) == len(checks):
        accepted = True
    elif decided and not all(decided):
        accepted = False

    return {
        "checks": checks,
        "accepted": accepted,
        "rubric_scores": None,
        "defect_severity": None,
        "human_repair_minutes": None,
    }


# --------------------------------------------------------------------------- #
# offline self-check
# --------------------------------------------------------------------------- #


def stub_agent_config(directory: pathlib.Path) -> dict:
    """A local stub that fakes an agent, so the plan, the record shape and the
    scorer can be exercised without credentials, network or money."""
    stub = directory / "stub_agent.py"
    stub.write_text(
        "\n".join(
            [
                "import json, os, sys",
                "sys.stdin.read()",
                "arm = os.environ['REPOCTX_EVAL_ARM']",
                "base = {'bare': 40000, 'current': 26000, 'guarded': 21000}[arm]",
                "json.dump({",
                "    'uncached_input_tokens': base,",
                "    'cache_read_input_tokens': base // 4,",
                "    'cache_write_input_tokens': base // 10,",
                "    'output_tokens': 3000,",
                "    'workflow': {'tool_calls': 12, 'file_read_calls': 6, 'retries': 0},",
                "}, open(os.environ['REPOCTX_EVAL_USAGE'], 'w'))",
                "print('stub run complete')",
            ]
        ),
        encoding="utf-8",
    )
    return {
        "command": [sys.executable, str(stub)],
        "repoctx": shutil.which("repoctx") or "repoctx",
        "repoctx_version": "stub",
        "agent_client_version": "stub",
        "model_id": "stub",
        "billing_mode": "stub",
        "pricing": {"source": "stub", "date": "2026-09-12", "currency": "USD"},
        "timeout_seconds": 120,
        # The self-check proves the harness, not a repository's test suite: it
        # must never report an acceptance it did not actually observe.
        "skip_acceptance_commands": True,
    }


# --------------------------------------------------------------------------- #
# entry point
# --------------------------------------------------------------------------- #


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=pathlib.Path)
    parser.add_argument("--out", type=pathlib.Path, default=HERE / "runs.jsonl")
    parser.add_argument("--check", action="store_true", help="report prerequisites and exit")
    parser.add_argument("--plan", action="store_true", help="print the run plan and exit")
    parser.add_argument(
        "--stub-agent",
        action="store_true",
        help="run the plan against a local stub agent (no network, no credentials)",
    )
    parser.add_argument("--repetitions", type=int)
    parser.add_argument("--task", action="append", help="restrict to these task ids")
    args = parser.parse_args(argv)

    manifest = load_manifest()
    config = json.loads(args.config.read_text(encoding="utf-8")) if args.config else None

    if args.plan:
        for run in plan_runs(manifest, args.repetitions):
            print(json.dumps(run, sort_keys=True))
        return 0

    if args.check:
        missing = check_prerequisites(manifest, config)
        if not missing:
            print("prerequisites: complete")
            return 0
        print("prerequisites missing:")
        for item in missing:
            print(f"  - {item}")
        return 1

    with tempfile.TemporaryDirectory() as scratch:
        scratch_path = pathlib.Path(scratch)
        if args.stub_agent:
            config = stub_agent_config(scratch_path)
        if config is None:
            raise PrerequisiteError(
                "a real comparison needs --config; use --stub-agent for the offline self-check"
            )
        if not args.stub_agent:
            missing = check_prerequisites(manifest, config)
            if missing:
                raise PrerequisiteError("; ".join(missing))

        tasks = {task["id"]: task for task in manifest["tasks"]}
        arms = {arm["id"]: arm for arm in manifest["arms"]}
        repositories = manifest_repositories(manifest)
        runs = plan_runs(manifest, args.repetitions)
        if args.task:
            runs = [run for run in runs if run["task_id"] in set(args.task)]

        args.out.parent.mkdir(parents=True, exist_ok=True)
        artifacts = args.out.with_suffix(".artifacts").resolve()
        if args.out.exists() or artifacts.exists():
            raise PrerequisiteError("output or artifacts already exist; choose a new --out")
        artifacts.mkdir()
        with args.out.open("x", encoding="utf-8") as handle:
            for index, run in enumerate(runs):
                task = tasks[run["task_id"]]
                workspace = artifacts / f"run-{index:04d}"
                record = run_one(
                    run, task, arms[run["arm_id"]], repositories[task["repository"]],
                    config, workspace,
                )
                record["manifest_sha256"] = manifest["_manifest_sha256"]
                handle.write(json.dumps(record, sort_keys=True) + "\n")
                handle.flush()  # Preserve all evidence for rubric review and interruption recovery.

    print(f"wrote {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
