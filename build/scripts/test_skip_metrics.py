#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.

"""
Tests for skip-metrics.py's baseline ratchet (compare_baseline).

Run: python3 build/scripts/test_skip_metrics.py
  or: python3 -m pytest build/scripts/test_skip_metrics.py -v

The ratchet is strict — no slack margin — because skip classification is
generator-deterministic (read from binding-report.json, not build/environment
state). Any per-reason skip count above baseline (e.g. MissingWrapperSymbol),
a total skip increase, or an emitted-count drop is a hard regression;
improvements never fail.
"""

import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path

_spec = importlib.util.spec_from_file_location(
    "skip_metrics", str(Path(__file__).parent / "skip-metrics.py")
)
skip_metrics = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(skip_metrics)

compare_baseline = skip_metrics.compare_baseline


def _metrics(skipped=100, emitted=1000, reasons=None):
    return {
        "summary": {"skipped_members": skipped, "emitted_members": emitted},
        "skip_reasons": reasons or {"MissingWrapperSymbol": 64, "UnsupportedSignature": 1420},
    }


class CompareBaselineTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.NamedTemporaryFile(
            mode="w", suffix=".json", delete=False)
        json.dump(_metrics(), self._tmp)
        self._tmp.close()
        self.baseline_path = self._tmp.name

    def tearDown(self):
        os.unlink(self.baseline_path)

    def test_flat_no_regression(self):
        reg, imp = compare_baseline(_metrics(), self.baseline_path)
        self.assertEqual(reg, [])
        self.assertEqual(imp, [])

    def test_per_reason_growth_by_one_is_regression_no_slack(self):
        # +1 on any reason must fail — the old +5 slack is gone.
        m = _metrics(reasons={"MissingWrapperSymbol": 65, "UnsupportedSignature": 1420})
        reg, _ = compare_baseline(m, self.baseline_path)
        self.assertTrue(any("MissingWrapperSymbol" in r for r in reg))

    def test_growth_within_old_slack_margin_now_fails(self):
        # +5 (previously silently absorbed) is now a regression.
        m = _metrics(reasons={"MissingWrapperSymbol": 69, "UnsupportedSignature": 1420})
        reg, _ = compare_baseline(m, self.baseline_path)
        self.assertTrue(any("MissingWrapperSymbol" in r for r in reg))

    def test_new_reason_is_regression(self):
        m = _metrics(reasons={"MissingWrapperSymbol": 64,
                              "UnsupportedSignature": 1420,
                              "BrandNewReason": 1})
        reg, _ = compare_baseline(m, self.baseline_path)
        self.assertTrue(any("BrandNewReason" in r for r in reg))

    def test_total_skip_increase_is_regression(self):
        reg, _ = compare_baseline(_metrics(skipped=101), self.baseline_path)
        self.assertTrue(any("Skip count increased" in r for r in reg))

    def test_emitted_decrease_is_regression(self):
        reg, _ = compare_baseline(_metrics(emitted=999), self.baseline_path)
        self.assertTrue(any("Emitted count decreased" in r for r in reg))

    def test_improvement_is_not_regression(self):
        m = _metrics(skipped=90,
                     reasons={"MissingWrapperSymbol": 60, "UnsupportedSignature": 1420})
        reg, imp = compare_baseline(m, self.baseline_path)
        self.assertEqual(reg, [])
        self.assertTrue(len(imp) >= 1)

    def test_missing_baseline_does_not_fail(self):
        reg, imp = compare_baseline(_metrics(), "/nonexistent/baseline.json")
        self.assertEqual(reg, [])
        self.assertEqual(len(imp), 1)


if __name__ == "__main__":
    unittest.main()
