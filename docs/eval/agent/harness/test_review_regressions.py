"""Regression checks for trustworthy comparison runs; no agent calls."""
import copy
import json
import pathlib
import tempfile
import unittest
from unittest import mock

import run_comparison as runner
import score_comparison as scorer

HERE = pathlib.Path(__file__).resolve().parent
PRICES = dict.fromkeys(scorer.SPEND_FIELDS, 1.0)


class ScoringRegressionTests(unittest.TestCase):
    def records(self):
        return scorer.load_runs(HERE / 'fixtures/runs-complete.jsonl')

    def test_empty_comparison_is_incomplete(self):
        self.assertFalse(scorer.score([], PRICES)['completeness']['comparison_complete'])

    def test_partial_fixture_is_not_a_complete_frozen_comparison(self):
        self.assertFalse(scorer.score(self.records(), PRICES)['completeness']['comparison_complete'])

    def test_incomplete_comparison_does_not_claim_lower_cost(self):
        claims = scorer.score(self.records(), PRICES)['claims']
        self.assertFalse(any('cost is lower' in c for c in claims))

    def test_subscription_is_not_actual_money(self):
        record = self.records()[0]
        record.update(billing_mode='subscription', billed_amount=1.0)
        self.assertIsNone(scorer.run_cost(record, PRICES))

    def test_invalid_billing_values_are_unknown(self):
        for amount in [-1, float('nan'), float('inf')]:
            record = self.records()[0]
            record['billed_amount'] = amount
            self.assertIsNone(scorer.run_cost(record, PRICES))

    def test_partial_spend_is_not_presented_as_total(self):
        runs = self.records()[:2]
        runs[1]['spend']['output_tokens'] = None
        self.assertIsNone(scorer.summarize_arm(runs, PRICES)['total_spend'])


class RunnerRegressionTests(unittest.TestCase):
    def test_guarded_arm_explicitly_enforces(self):
        manifest = runner.load_manifest()
        arm = next(a for a in manifest['arms'] if a['id'] == 'guarded')
        commands = runner.arm_setup_commands(arm, 'repoctx')
        self.assertIn(['repoctx', 'integrate', '--client', 'claude-code', '--guard',
                       '--guard-mode', 'enforce'], commands)

    def test_preflight_detects_unimplemented_review_and_compaction_fixtures(self):
        missing = runner.check_prerequisites(runner.load_manifest(), None)
        self.assertTrue(any('scenario' in m or 'patch' in m for m in missing))

    def test_preflight_rejects_raw_claude_as_usage_adapter(self):
        config = {'command': ['claude', '-p', '--output-format', 'json'], 'pricing': {'source': 'x'}}
        missing = runner.check_prerequisites(runner.load_manifest(), config)
        self.assertTrue(any('usage adapter' in m for m in missing))


if __name__ == '__main__':
    unittest.main()
