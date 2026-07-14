#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.

"""
Tests for coverage-report.py's baseline ratchet (compare_coverage_baseline).

Run: python3 build/scripts/test_coverage_report.py
  or: python3 -m pytest build/scripts/test_coverage_report.py -v

The ratchet is what makes BindingTests/baselines.json's coverage budgets
enforced rather than advisory: any degraded / compiled-out / passing-untested /
known-unsupported count above its committed baseline must fail the run, while
improvements (counts below baseline) must NOT fail.
"""

import importlib.util
import os
import sys
import unittest
from pathlib import Path

# coverage-report.py has a hyphen, so load it by path rather than `import`.
_spec = importlib.util.spec_from_file_location(
    "coverage_report", str(Path(__file__).parent / "coverage-report.py")
)
coverage_report = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(coverage_report)

compare_coverage_baseline = coverage_report.compare_coverage_baseline


def _summary(degraded=0, compiled_out=0, passing_untested=0, ku_total=0):
    return {
        "must_pass": {
            "total": 100,
            "passing": 90,
            "degraded": degraded,
            "compiled_out": compiled_out,
            "missing": 0,
            "passing_untested": passing_untested,
        },
        "known_unsupported": {
            "total": ku_total,
            "with_test": ku_total,
            "compiled_out": 0,
            "without_test": 0,
        },
    }


BASELINE = {
    "must_pass_degraded": 1,
    "must_pass_compiled_out": 9,
    "must_pass_passing_untested": 33,
    "known_unsupported_total": 62,
}


class CompareCoverageBaselineTests(unittest.TestCase):
    def test_flat_at_baseline_no_regression(self):
        reg, imp = compare_coverage_baseline(
            _summary(1, 9, 33, 62), BASELINE)
        self.assertEqual(reg, [])
        self.assertEqual(imp, [])

    def test_degraded_growth_is_regression(self):
        reg, imp = compare_coverage_baseline(
            _summary(2, 9, 33, 62), BASELINE)
        self.assertEqual(len(reg), 1)
        self.assertIn("must_pass_degraded", reg[0])
        self.assertIn("+1", reg[0])

    def test_compiled_out_growth_is_regression(self):
        reg, _ = compare_coverage_baseline(
            _summary(1, 12, 33, 62), BASELINE)
        self.assertTrue(any("must_pass_compiled_out" in r for r in reg))

    def test_untested_growth_is_regression(self):
        reg, _ = compare_coverage_baseline(
            _summary(1, 9, 40, 62), BASELINE)
        self.assertTrue(any("must_pass_passing_untested" in r for r in reg))

    def test_known_unsupported_growth_is_regression(self):
        reg, _ = compare_coverage_baseline(
            _summary(1, 9, 33, 70), BASELINE)
        self.assertTrue(any("known_unsupported_total" in r for r in reg))

    def test_improvement_is_not_a_regression(self):
        reg, imp = compare_coverage_baseline(
            _summary(0, 5, 30, 60), BASELINE)
        self.assertEqual(reg, [])
        # All four dropped below baseline -> four improvements reported.
        self.assertEqual(len(imp), 4)

    def test_multiple_regressions_all_reported(self):
        reg, _ = compare_coverage_baseline(
            _summary(3, 15, 40, 62), BASELINE)
        self.assertEqual(len(reg), 3)

    def test_missing_key_is_skipped_not_treated_as_zero(self):
        # A partial baseline must not spuriously fail on the absent key.
        partial = {"must_pass_degraded": 1}
        reg, imp = compare_coverage_baseline(
            _summary(1, 99, 99, 99), partial)
        self.assertEqual(reg, [])
        self.assertEqual(imp, [])

    def test_committed_baseline_file_is_consistent_with_ratchet_keys(self):
        # Every ratchet key must exist in the shipped baselines.json so the gate
        # is actually enforced (a dropped key silently disables its ratchet).
        baselines_path = (Path(__file__).resolve().parents[2]
                          / "BindingTests" / "baselines.json")
        if not baselines_path.is_file():
            self.skipTest("baselines.json not found (running outside repo tree)")
        import json
        data = json.loads(baselines_path.read_text())
        for key in coverage_report.COVERAGE_RATCHET_KEYS:
            self.assertIn(key, data,
                          f"{key} missing from baselines.json — ratchet disabled")
        # generator_exit_code must NOT be reintroduced (redundant with the
        # regen-exit gate; deliberately deleted).
        self.assertNotIn("generator_exit_code", data,
                         "generator_exit_code is enforced by the regen gate; "
                         "do not re-add it as a dead key")


if __name__ == "__main__":
    unittest.main()
