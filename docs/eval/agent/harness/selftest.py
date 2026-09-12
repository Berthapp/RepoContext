#!/usr/bin/env python3
"""Offline self-check for the agent comparison harness.

No agent, no credentials, no network, no money: this exercises the run plan, the
prerequisite report, the run-record shape and the whole scoring path against
public synthetic fixtures. It exists so the harness is a finished, executable
setup even where real agent credentials are unavailable -- and so a later change
to the accounting rules is caught here instead of during a paid comparison.

    python3 selftest.py
"""

from __future__ import annotations

import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import run_comparison as runner  # noqa: E402
import score_comparison as scorer  # noqa: E402

FIXTURES = HERE / "fixtures"
PRICES = {
    "uncached_input_tokens": 3.0,
    "cache_read_input_tokens": 0.3,
    "cache_write_input_tokens": 3.75,
    "output_tokens": 15.0,
    "reasoning_tokens_billed_separately": 15.0,
}

failures: list[str] = []


def check(condition: bool, message: str) -> None:
    if not condition:
        failures.append(message)


def load(name: str) -> list[dict]:
    return scorer.load_runs(FIXTURES / name)


def test_manifest_is_frozen_and_well_formed() -> None:
    manifest = runner.load_manifest()
    arms = [arm["id"] for arm in manifest["arms"]]
    check(arms == ["bare", "current", "guarded"], "the three arms must be present in order")
    check(len(manifest["tasks"]) >= 10, "the manifest must carry a real task set")
    kinds = {task["kind"] for task in manifest["tasks"]}
    check(
        kinds >= {"fix", "feature", "refactor", "explain", "review"},
        f"every task kind must appear, found {sorted(kinds)}",
    )
    languages = {task["language"] for task in manifest["tasks"]}
    check(
        languages >= {"csharp", "typescript", "javascript"},
        f"C#, TypeScript and JavaScript must appear, found {sorted(languages)}",
    )
    scenarios = {s for task in manifest["tasks"] for s in task["scenario"]}
    check(
        scenarios >= {"long_file", "cross_file", "stale_index", "compaction"},
        f"the plan's scenarios must appear, found {sorted(scenarios)}",
    )
    check(
        any(task["holdout"] for task in manifest["tasks"]),
        "a holdout must be retained",
    )
    ids = [task["id"] for task in manifest["tasks"]]
    check(len(ids) == len(set(ids)), "task ids must be unique")
    known_repositories = {repo["id"] for repo in manifest["repositories"]}
    check(
        all(task["repository"] in known_repositories for task in manifest["tasks"]),
        "every task must name a frozen repository",
    )


def test_plan_is_paired_and_counterbalanced() -> None:
    manifest = runner.load_manifest()
    runs = runner.plan_runs(manifest, repetitions=3)
    tasks = len(manifest["tasks"])
    check(len(runs) == tasks * 3 * 3, "plan must be tasks x repetitions x arms")
    positions: dict[str, set[int]] = {}
    for run in runs:
        positions.setdefault(run["arm_id"], set()).add(run["arm_position"])
    check(
        all(len(seen) == 3 for seen in positions.values()),
        f"each arm must occupy every order position, got {positions}",
    )
    first = [run for run in runs if run["repetition"] == 1]
    check(
        all(run["cache_mode"] == "cold" for run in first),
        "the first repetition of every pair must be cold",
    )


def test_prerequisites_name_the_missing_credential() -> None:
    manifest = runner.load_manifest()
    missing = runner.check_prerequisites(manifest, None)
    check(any("--config" in item for item in missing), "a missing config must be named")

    config = {"command": ["agent"], "required_env": ["EVAL_FAKE_TOKEN_UNSET"], "pricing": {}}
    missing = runner.check_prerequisites(manifest, config)
    check(
        any("EVAL_FAKE_TOKEN_UNSET" in item for item in missing),
        "an unset agent credential must be named explicitly",
    )
    check(
        any("pricing" in item for item in missing),
        "a missing price sheet must be named explicitly",
    )


def test_archives_match_the_frozen_hashes() -> None:
    manifest = runner.load_manifest()
    missing = runner.check_prerequisites(manifest, None)
    check(
        not any("sha256" in item for item in missing),
        f"frozen source archives must still hash as recorded: {missing}",
    )


def test_incomplete_usage_blocks_every_saving_claim() -> None:
    report = scorer.score(load("runs-incomplete.jsonl"), PRICES)
    check(
        report["completeness"]["comparison_complete"] is False,
        "a run set with holes must be reported incomplete",
    )
    check(
        any(claim.startswith("INCOMPLETE") for claim in report["claims"]),
        "an incomplete run set must say so in its claims",
    )
    check(
        report["arms"]["guarded"]["total_spend_complete"] is False,
        "an arm with an unknown run cost must not report a complete spend total",
    )
    check(
        report["arms"]["guarded"]["cost_per_accepted_completion"] is None,
        "cost per accepted completion needs complete billing data",
    )


def test_failures_stay_in_the_cost_denominator() -> None:
    runs = load("runs-complete.jsonl")
    report = scorer.score(runs, PRICES)
    guarded = report["arms"]["guarded"]
    check(guarded["runs"] == 6, f"all guarded runs must be counted, got {guarded['runs']}")
    check(guarded["accepted"] == 5, f"one guarded run must be rejected, got {guarded['accepted']}")
    spend_of_failed = next(
        scorer.run_cost(record, PRICES)
        for record in runs
        if record["arm_id"] == "guarded" and not record["quality"]["accepted"]
    )
    check(spend_of_failed > 0, "the failed run must still have a cost")
    check(
        abs(guarded["total_spend"] - sum(
            scorer.run_cost(record, PRICES) for record in runs if record["arm_id"] == "guarded"
        )) < 1e-9,
        "spend on failed runs must stay in the total",
    )
    check(
        abs(guarded["cost_per_accepted_completion"] - guarded["total_spend"] / 5) < 1e-9,
        "cost per accepted completion divides total spend by accepted runs only",
    )


def test_no_acceptance_makes_the_ratio_undefined_not_zero() -> None:
    runs = load("runs-complete.jsonl")
    for record in runs:
        if record["arm_id"] == "bare":
            record["quality"]["accepted"] = False
    report = scorer.score(runs, PRICES)
    bare = report["arms"]["bare"]
    check(
        bare["cost_per_accepted_completion"] is None,
        "zero accepted completions must not produce a cost of zero",
    )
    check(
        bare["cost_per_accepted_completion_undefined_reason"] == "no accepted completion",
        "the undefined reason must be explicit",
    )


def test_reasoning_tokens_are_not_double_counted() -> None:
    record = {
        "billed_amount": None,
        "spend": {
            "uncached_input_tokens": 1_000_000,
            "cache_read_input_tokens": 0,
            "cache_write_input_tokens": 0,
            "output_tokens": 1_000_000,
            "reasoning_tokens_billed_separately": None,
        },
    }
    check(
        abs(scorer.run_cost(record, PRICES) - 18.0) < 1e-9,
        "absent separately-billed reasoning tokens must add nothing",
    )
    record["spend"]["reasoning_tokens_billed_separately"] = 1_000_000
    check(
        abs(scorer.run_cost(record, PRICES) - 33.0) < 1e-9,
        "separately billed reasoning tokens are added exactly once",
    )


def test_paired_delta_and_regression_bound() -> None:
    report = scorer.score(load("runs-complete.jsonl"), PRICES)
    paired = report["paired_vs_current"]["guarded"]
    check(paired["cost_delta"]["n"] == 6, "every repetition must pair against the baseline")
    check(paired["cost_delta"]["mean"] < 0, "the fixture's guarded arm is cheaper")
    check(
        paired["largest_acceptance_regression_compatible_with_data"] is not None,
        "the largest compatible quality regression must be reported",
    )
    check(
        paired["largest_acceptance_regression_compatible_with_data"] > 0,
        "a fixture with one guarded failure must admit a possible quality regression",
    )


def test_per_task_and_cache_modes_are_reported_separately() -> None:
    report = scorer.score(load("runs-complete.jsonl"), PRICES)
    check(len(report["per_task"]) >= 2, "per-task results must be reported")
    check(
        report["cache_modes"]["cold"]["current"]["runs"] > 0
        and report["cache_modes"]["warm"]["current"]["runs"] > 0,
        "cold and warm workloads must be summarized separately",
    )
    check(
        report["cache_modes"]["cold"]["current"]["runs"]
        + report["cache_modes"]["warm"]["current"]["runs"]
        == report["arms"]["current"]["runs"],
        "cache-mode partitions must cover every run exactly once",
    )


def test_markdown_report_renders() -> None:
    report = scorer.score(load("runs-complete.jsonl"), PRICES)
    text = scorer.render_markdown(report)
    check("Supported claims" in text, "the report must carry its supported claims")
    check("Cost / accepted" in text, "the report must carry cost per accepted completion")


def test_stub_run_produces_valid_records(tmp: pathlib.Path) -> None:
    out = tmp / "runs.jsonl"
    code = runner.main(
        [
            "--stub-agent",
            "--repetitions",
            "1",
            "--task",
            "refactor-js-limit-internals",
            "--out",
            str(out),
        ]
    )
    check(code == 0, "the stub run must succeed")
    records = [json.loads(line) for line in out.read_text(encoding="utf-8").splitlines()]
    check(len(records) == 3, f"one repetition must produce three arms, got {len(records)}")
    for record in records:
        for field in runner.REQUIRED_PROVENANCE:
            check(field in record, f"run record must carry {field}")
        check("transcript_sha256" in record, "run record must carry a transcript digest")
        check(
            record["quality"]["rubric_scores"] is None,
            "an unadjudicated run must not invent rubric scores",
        )


def main() -> int:
    import tempfile

    test_manifest_is_frozen_and_well_formed()
    test_plan_is_paired_and_counterbalanced()
    test_prerequisites_name_the_missing_credential()
    test_archives_match_the_frozen_hashes()
    test_incomplete_usage_blocks_every_saving_claim()
    test_failures_stay_in_the_cost_denominator()
    test_no_acceptance_makes_the_ratio_undefined_not_zero()
    test_reasoning_tokens_are_not_double_counted()
    test_paired_delta_and_regression_bound()
    test_per_task_and_cache_modes_are_reported_separately()
    test_markdown_report_renders()
    with tempfile.TemporaryDirectory() as scratch:
        test_stub_run_produces_valid_records(pathlib.Path(scratch))

    if failures:
        for failure in failures:
            print(f"FAIL: {failure}")
        return 1
    print("agent-harness self-check: ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
