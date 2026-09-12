#!/usr/bin/env python3
"""Scores a recorded three-arm comparison (milestone 1 / milestone 5).

Reads the ``runs.jsonl`` written by ``run_comparison.py`` and produces a report
that a release decision can actually rest on:

* per-task **and** aggregate results, so a gain on easy tasks cannot hide damage
  on hard ones,
* paired differences against the ``current`` arm with a deterministic bootstrap
  confidence interval, including the largest quality regression still compatible
  with the data,
* total spend and cost per accepted completion, with spend on failed and retried
  runs kept in the numerator,
* an explicit completeness section: every missing usage field, unscored rubric
  and absent price is named. A comparison with holes is reported as incomplete,
  never rounded into a saving.

    python3 score_comparison.py runs.jsonl --prices prices.json -o report.json
"""

from __future__ import annotations

import argparse
import json
import math
import pathlib
import random
import statistics
import sys

HERE = pathlib.Path(__file__).resolve().parent
MANIFEST = HERE.parent / "manifest.json"

BOOTSTRAP_SAMPLES = 10_000
BOOTSTRAP_SEED = 20260912
BASELINE_ARM = "current"

SPEND_FIELDS = (
    "uncached_input_tokens",
    "cache_read_input_tokens",
    "cache_write_input_tokens",
    "output_tokens",
    "reasoning_tokens_billed_separately",
)


def load_runs(path: pathlib.Path) -> list[dict]:
    runs = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            runs.append(json.loads(line))
    return runs


def run_cost(record: dict, prices: dict | None) -> float | None:
    """Money for one run.

    A provider-reported ``billed_amount`` wins. Otherwise the disjoint billing
    categories are priced with the supplied price sheet. Reasoning tokens are
    only added when the record says the provider bills them outside the output
    category, so they are never counted twice. Any missing input returns None:
    an unknown cost stays unknown.
    """
    if record.get("billed_amount") is not None:
        return float(record["billed_amount"])
    if not prices:
        return None
    spend = record.get("spend") or {}
    total = 0.0
    for field in SPEND_FIELDS:
        value = spend.get(field)
        if value is None:
            if field == "reasoning_tokens_billed_separately":
                continue
            return None
        rate = prices.get(field)
        if rate is None:
            return None
        total += (value / 1_000_000.0) * rate
    return total


def bootstrap_ci(paired: list[float], confidence: float = 0.95) -> dict:
    """Percentile bootstrap over paired differences, deterministic by seed."""
    if not paired:
        return {"n": 0, "mean": None, "low": None, "high": None}
    rng = random.Random(BOOTSTRAP_SEED)
    n = len(paired)
    means = []
    for _ in range(BOOTSTRAP_SAMPLES):
        means.append(sum(paired[rng.randrange(n)] for _ in range(n)) / n)
    means.sort()
    tail = (1.0 - confidence) / 2.0
    return {
        "n": n,
        "mean": statistics.fmean(paired),
        "low": means[int(math.floor(tail * (BOOTSTRAP_SAMPLES - 1)))],
        "high": means[int(math.ceil((1.0 - tail) * (BOOTSTRAP_SAMPLES - 1)))],
    }


def acceptance_value(record: dict) -> float | None:
    accepted = (record.get("quality") or {}).get("accepted")
    return None if accepted is None else (1.0 if accepted else 0.0)


def rubric_value(record: dict) -> float | None:
    scores = (record.get("quality") or {}).get("rubric_scores")
    if not scores:
        return None
    return statistics.fmean(float(v) for v in scores.values())


def pair_by_run(runs: list[dict], arm: str, baseline: str, value) -> list[float]:
    """Pairs runs of two arms on (task, repetition, cache mode)."""
    keyed: dict[tuple, dict[str, float]] = {}
    for record in runs:
        key = (record["task_id"], record["repetition"], record["cache_mode"])
        observed = value(record)
        if observed is None:
            continue
        keyed.setdefault(key, {})[record["arm_id"]] = observed
    return [
        sides[arm] - sides[baseline]
        for sides in keyed.values()
        if arm in sides and baseline in sides
    ]


def summarize_arm(runs: list[dict], prices: dict | None) -> dict:
    costs = [run_cost(record, prices) for record in runs]
    known = [cost for cost in costs if cost is not None]
    accepted = [record for record in runs if (record.get("quality") or {}).get("accepted")]
    severities = [
        (record.get("quality") or {}).get("defect_severity") for record in runs
    ]
    repair = [
        (record.get("quality") or {}).get("human_repair_minutes")
        for record in runs
        if (record.get("quality") or {}).get("human_repair_minutes") is not None
    ]
    unscored = sum(
        1 for record in runs if (record.get("quality") or {}).get("accepted") is None
    )

    total_cost = sum(known) if known else None
    complete_cost = len(known) == len(costs) and bool(costs)
    return {
        "runs": len(runs),
        "accepted": len(accepted),
        "unscored_runs": unscored,
        "timeouts": sum(1 for record in runs if record.get("timed_out")),
        "acceptance_rate": (len(accepted) / len(runs)) if runs else None,
        "severe_defects": sum(1 for s in severities if s == "severe"),
        "severity_unscored": sum(1 for s in severities if s is None),
        "human_repair_minutes_total": sum(repair) if repair else None,
        "total_spend": total_cost,
        "total_spend_complete": complete_cost,
        "cost_per_accepted_completion": (
            None
            if not complete_cost or not accepted
            else total_cost / len(accepted)
        ),
        "cost_per_accepted_completion_undefined_reason": (
            None
            if complete_cost and accepted
            else ("no accepted completion" if complete_cost else "incomplete billing data")
        ),
        "tokens": {
            field: (
                sum(
                    (record.get("spend") or {}).get(field) or 0 for record in runs
                )
                if all(
                    (record.get("spend") or {}).get(field) is not None for record in runs
                )
                else None
            )
            for field in SPEND_FIELDS
        },
        "workflow": {
            key: sum((record.get("workflow") or {}).get(key) or 0 for record in runs)
            for key in ("tool_calls", "file_read_calls", "retries", "compactions")
        },
        "wall_clock_seconds_total": sum(
            record.get("wall_clock_seconds") or 0.0 for record in runs
        ),
    }


def completeness(runs: list[dict], prices: dict | None) -> dict:
    missing_usage = sorted(
        {
            f"{record['run_id']}:{field}"
            for record in runs
            for field in SPEND_FIELDS
            if field != "reasoning_tokens_billed_separately"
            and (record.get("spend") or {}).get(field) is None
        }
    )
    missing_rubric = sorted(
        record["run_id"]
        for record in runs
        if (record.get("quality") or {}).get("rubric_scores") is None
    )
    missing_severity = sorted(
        record["run_id"]
        for record in runs
        if (record.get("quality") or {}).get("defect_severity") is None
    )
    subscription = sorted(
        {record["run_id"] for record in runs if record.get("billing_mode") == "subscription"}
    )
    return {
        "priced": bool(prices) or all(r.get("billed_amount") is not None for r in runs),
        "missing_usage_fields": missing_usage,
        "unscored_rubric_runs": missing_rubric,
        "unscored_severity_runs": missing_severity,
        "subscription_runs_excluded_from_money": subscription,
        "comparison_complete": not missing_usage and not missing_rubric,
    }


def score(runs: list[dict], prices: dict | None) -> dict:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    tasks = {task["id"]: task for task in manifest["tasks"]}
    arms = [arm["id"] for arm in manifest["arms"]]

    report: dict = {
        "schema_version": 1,
        "manifest_id": manifest["id"],
        "baseline_arm": BASELINE_ARM,
        "bootstrap": {"samples": BOOTSTRAP_SAMPLES, "seed": BOOTSTRAP_SEED},
        "arms": {},
        "per_task": {},
        "paired_vs_current": {},
        "cache_modes": {},
        "completeness": completeness(runs, prices),
        "claims": [],
    }

    for arm in arms:
        arm_runs = [record for record in runs if record["arm_id"] == arm]
        report["arms"][arm] = summarize_arm(arm_runs, prices)

    for mode in ("cold", "warm"):
        report["cache_modes"][mode] = {
            arm: summarize_arm(
                [r for r in runs if r["arm_id"] == arm and r["cache_mode"] == mode],
                prices,
            )
            for arm in arms
        }

    for task_id in tasks:
        task_runs = [record for record in runs if record["task_id"] == task_id]
        if not task_runs:
            continue
        report["per_task"][task_id] = {
            "holdout": tasks[task_id]["holdout"],
            "kind": tasks[task_id]["kind"],
            "arms": {
                arm: summarize_arm(
                    [r for r in task_runs if r["arm_id"] == arm], prices
                )
                for arm in arms
            },
        }

    for arm in arms:
        if arm == BASELINE_ARM:
            continue
        acceptance = bootstrap_ci(pair_by_run(runs, arm, BASELINE_ARM, acceptance_value))
        rubric = bootstrap_ci(pair_by_run(runs, arm, BASELINE_ARM, rubric_value))
        cost = bootstrap_ci(
            pair_by_run(runs, arm, BASELINE_ARM, lambda r: run_cost(r, prices))
        )
        report["paired_vs_current"][arm] = {
            "acceptance_delta": acceptance,
            "rubric_delta": rubric,
            "cost_delta": cost,
            "largest_acceptance_regression_compatible_with_data": (
                None if acceptance["low"] is None else -min(0.0, acceptance["low"])
            ),
            "largest_rubric_regression_compatible_with_data": (
                None if rubric["low"] is None else -min(0.0, rubric["low"])
            ),
        }

    report["claims"] = supported_claims(report)
    return report


def supported_claims(report: dict) -> list[str]:
    """The only sentences this data licenses. Everything else is unmeasured."""
    claims: list[str] = []
    complete = report["completeness"]["comparison_complete"]
    if not complete:
        claims.append(
            "INCOMPLETE: usage or rubric data is missing; no saving and no equal-quality "
            "claim is supported by this run set."
        )
    for arm, paired in report["paired_vs_current"].items():
        cost = paired["cost_delta"]
        acceptance = paired["acceptance_delta"]
        if cost["mean"] is None:
            claims.append(f"{arm}: cost not measurable from these runs.")
            continue
        direction = "lower" if cost["mean"] < 0 else "higher"
        significant = cost["high"] is not None and cost["high"] < 0
        claims.append(
            f"{arm}: paired cost is {direction} than {BASELINE_ARM} by "
            f"{abs(cost['mean']):.6g} per run "
            f"(95% CI {cost['low']:.6g}..{cost['high']:.6g})"
            + ("; the interval excludes zero." if significant else "; the interval includes zero.")
        )
        if acceptance["n"] == 0:
            claims.append(f"{arm}: acceptance not adjudicated; quality parity unestablished.")
        elif acceptance["low"] is not None and acceptance["low"] < 0:
            claims.append(
                f"{arm}: a quality regression of up to "
                f"{-acceptance['low']:.4g} acceptance points is still compatible with the data."
            )
    return claims


def render_markdown(report: dict) -> str:
    lines = [
        "# Agent comparison report",
        "",
        f"Manifest: `{report['manifest_id']}`. Baseline arm: `{report['baseline_arm']}`.",
        "",
        "## Arms",
        "",
        "| Arm | Runs | Accepted | Timeouts | Total spend | Cost / accepted |",
        "| --- | ---: | ---: | ---: | ---: | ---: |",
    ]
    for arm, summary in report["arms"].items():
        spend = summary["total_spend"]
        per = summary["cost_per_accepted_completion"]
        lines.append(
            f"| {arm} | {summary['runs']} | {summary['accepted']} | {summary['timeouts']} | "
            f"{'n/a' if spend is None else f'{spend:.4f}'} | "
            f"{'undefined' if per is None else f'{per:.4f}'} |"
        )
    lines += ["", "## Supported claims", ""]
    lines += [f"* {claim}" for claim in report["claims"]] or ["* none"]
    lines += ["", "## Completeness", ""]
    completeness_section = report["completeness"]
    lines.append(f"* comparison complete: {completeness_section['comparison_complete']}")
    lines.append(
        f"* missing usage fields: {len(completeness_section['missing_usage_fields'])}"
    )
    lines.append(
        f"* runs without rubric scores: {len(completeness_section['unscored_rubric_runs'])}"
    )
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runs", type=pathlib.Path)
    parser.add_argument("--prices", type=pathlib.Path)
    parser.add_argument("-o", "--out", type=pathlib.Path)
    parser.add_argument("--markdown", type=pathlib.Path)
    args = parser.parse_args(argv)

    runs = load_runs(args.runs)
    prices = json.loads(args.prices.read_text(encoding="utf-8")) if args.prices else None
    report = score(runs, prices)

    text = json.dumps(report, indent=2, sort_keys=True) + "\n"
    if args.out:
        args.out.write_text(text, encoding="utf-8")
    else:
        sys.stdout.write(text)
    if args.markdown:
        args.markdown.write_text(render_markdown(report), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
