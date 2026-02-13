#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Compare current pipeline outputs against baselines.json.
# Exit non-zero if any baseline is exceeded.
#
# Usage: ./check-baselines.sh

set -e
cd "$(dirname "$0")"

BASELINE="baselines.json"
COVERAGE="output/coverage-matrix.json"
EXIT_CODE_FILE="output/generator-exit-code"
STRIP_COUNT_FILE="output/wrapper-stripped-count"

FAIL=0

# Helper: read JSON field
jval() { python3 -c "import json; print(json.load(open('$1'))$2)"; }

# Generator exit code
expected_exit=$(jval "$BASELINE" "['generator_exit_code']")
actual_exit=$(cat "$EXIT_CODE_FILE" 2>/dev/null || echo "MISSING")
if [ "$actual_exit" = "MISSING" ]; then
    echo "BASELINE FAIL: generator_exit_code file missing"
    FAIL=1
elif [ "$actual_exit" != "$expected_exit" ]; then
    echo "BASELINE FAIL: generator_exit_code: expected=$expected_exit actual=$actual_exit"
    FAIL=1
fi

# Coverage degraded count
expected_degraded=$(jval "$BASELINE" "['must_pass_degraded']")
actual_degraded=$(jval "$COVERAGE" "['summary']['must_pass']['degraded']" 2>/dev/null || echo "MISSING")
if [ "$actual_degraded" = "MISSING" ]; then
    echo "BASELINE FAIL: coverage-matrix.json missing or unreadable"
    FAIL=1
elif [ "$actual_degraded" -gt "$expected_degraded" ] 2>/dev/null; then
    echo "BASELINE FAIL: must_pass_degraded: expected<=$expected_degraded actual=$actual_degraded"
    FAIL=1
fi

# Compiled-out count (should not increase)
expected_co=$(jval "$BASELINE" "['must_pass_compiled_out']")
actual_co=$(jval "$COVERAGE" "['summary']['must_pass']['compiled_out']" 2>/dev/null || echo "MISSING")
if [ "$actual_co" = "MISSING" ]; then
    echo "BASELINE FAIL: must_pass_compiled_out not found in coverage-matrix.json"
    FAIL=1
elif [ "$actual_co" -gt "$expected_co" ] 2>/dev/null; then
    echo "BASELINE FAIL: must_pass_compiled_out: expected<=$expected_co actual=$actual_co"
    FAIL=1
fi

# Known unsupported total (should not increase)
expected_unsup=$(jval "$BASELINE" "['known_unsupported_total']")
actual_unsup=$(jval "$COVERAGE" "['summary']['known_unsupported']['total']" 2>/dev/null || echo "MISSING")
if [ "$actual_unsup" = "MISSING" ]; then
    echo "BASELINE FAIL: known_unsupported_total not found in coverage-matrix.json"
    FAIL=1
elif [ "$actual_unsup" -gt "$expected_unsup" ] 2>/dev/null; then
    echo "BASELINE FAIL: known_unsupported_total: expected<=$expected_unsup actual=$actual_unsup"
    FAIL=1
fi

# CrashRisk class count (use precise pattern to match actual attribute usage)
expected_crash=$(jval "$BASELINE" "['crash_risk_classes']")
actual_crash=$(grep -rl '\[CrashRisk("' RuntimeTestsApp/ --include="*.cs" 2>/dev/null | wc -l | tr -d ' ')
if [ "$actual_crash" -gt "$expected_crash" ] 2>/dev/null; then
    echo "BASELINE FAIL: crash_risk_classes: expected<=$expected_crash actual=$actual_crash"
    FAIL=1
fi

# Wrapper stripped count
expected_strip=$(jval "$BASELINE" "['wrapper_stripped_count']")
if [ ! -f "$STRIP_COUNT_FILE" ]; then
    echo "BASELINE WARN: wrapper-stripped-count file missing (async wrapper may not have been built)"
else
    actual_strip=$(cat "$STRIP_COUNT_FILE")
    tolerance=2
    max_strip=$((expected_strip + tolerance))
    if [ "$actual_strip" -gt "$max_strip" ] 2>/dev/null; then
        echo "BASELINE FAIL: wrapper_stripped_count: expected<=$max_strip actual=$actual_strip (baseline=$expected_strip +$tolerance tolerance)"
        FAIL=1
    fi
fi

if [ $FAIL -eq 0 ]; then
    echo "All baselines OK."
else
    echo ""
    echo "Baseline check failed. If these changes are intentional, update baselines.json."
    exit 1
fi
