#!/bin/bash
# Run all tests with proper dotnet host path workaround
#
# Runs all test suites and prints a final summary. Continues past failures
# so all suites are exercised — the summary at the end shows what failed.

cd "$(dirname "$0")"

DOTNET_PATH=$(which dotnet)

# Track results for final summary
declare -a SUITE_NAMES=()
declare -a SUITE_RESULTS=()
OVERALL_EXIT=0

run_suite() {
    local name="$1"
    shift
    SUITE_NAMES+=("$name")

    echo ""
    echo "=== $name ==="
    if "$@"; then
        SUITE_RESULTS+=("PASS")
        echo "--- $name: PASS ---"
    else
        local ec=$?
        SUITE_RESULTS+=("FAIL")
        OVERALL_EXIT=1
        echo ""
        echo "*** $name: FAIL (exit code $ec) ***"
    fi
}

print_summary() {
    echo ""
    echo "========================================"
    echo "         TEST SUITE SUMMARY"
    echo "========================================"
    local i
    for i in "${!SUITE_NAMES[@]}"; do
        local marker="PASS"
        if [ "${SUITE_RESULTS[$i]}" = "FAIL" ]; then
            marker="FAIL"
        fi
        printf "  %-40s %s\n" "${SUITE_NAMES[$i]}" "$marker"
    done
    echo "========================================"
    if [ "$OVERALL_EXIT" -ne 0 ]; then
        echo "  RESULT: FAILED"
    else
        echo "  RESULT: ALL PASSED"
    fi
    echo "========================================"
}

# Always print summary on exit (normal, error, or interrupt)
trap print_summary EXIT

# Build everything first
echo "=== Building Projects ==="
if ! ./build.sh; then
    echo "*** BUILD FAILED ***"
    SUITE_NAMES+=("Build")
    SUITE_RESULTS+=("FAIL")
    OVERALL_EXIT=1
    exit 1
fi

# Run each test suite — failures are tracked but don't stop other suites
run_suite "Unit Tests" \
    dotnet test src/Swift.Bindings/tests/UnitTests --no-build -c Debug \
    -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

run_suite "Runtime Tests" \
    dotnet test src/Swift.Runtime/tests --no-build -c Debug \
    -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

run_suite "Analyzer Tests" \
    dotnet test src/Swift.Analyzers.Tests --no-build -c Debug \
    -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

# BindingTests suite (macOS + Xcode only)
if [ "$(uname)" != "Darwin" ]; then
    echo ""
    echo "Skipping BindingTests (requires macOS with Xcode)."
elif [ ! -d "BindingTests" ]; then
    echo ""
    echo "BindingTests directory not found, skipping."
else
    run_suite "BindingTests Regression Suite" \
        bash -c 'cd BindingTests && ./build-and-test.sh --strict && ./generate-coverage-report.sh && ./check-baselines.sh'

    # Fail on degraded must-pass features (actual regressions)
    COVERAGE_JSON="BindingTests/output/coverage-matrix.json"
    if [ -f "$COVERAGE_JSON" ]; then
        DEGRADED=$(python3 -c "
import json, sys
with open('$COVERAGE_JSON') as f:
    data = json.load(f)
mp = data.get('summary', {}).get('must_pass', {})
print(mp.get('degraded', 0))
" 2>/dev/null || echo "0")
        if [ "$DEGRADED" -gt 0 ]; then
            echo ""
            echo "*** $DEGRADED must-pass feature(s) are degraded in BindingTests ***"
            echo "See $COVERAGE_JSON for details."
            OVERALL_EXIT=1
        fi
    fi

    # Run runtime tests on iOS Simulator (if available)
    if ! command -v xcrun &>/dev/null; then
        echo ""
        echo "Skipping BindingTests Runtime Tests (xcrun not available)."
    elif ! xcrun simctl list devices &>/dev/null 2>&1; then
        echo ""
        echo "Skipping BindingTests Runtime Tests (iOS Simulator runtime not available)."
    else
        HAS_SIM=$(xcrun simctl list devices available -j 2>/dev/null | python3 -c "
import json, sys
data = json.load(sys.stdin)
for runtime, devices in data.get('devices', {}).items():
    if 'iOS' not in runtime and 'iphone' not in runtime.lower():
        continue
    for d in devices:
        if d.get('isAvailable', False) and 'iPhone' in d.get('name', ''):
            print('yes'); sys.exit(0)
sys.exit(1)
" 2>/dev/null) || true

        if [ "$HAS_SIM" != "yes" ]; then
            echo ""
            echo "Skipping BindingTests Runtime Tests (no available iPhone simulator found)."
        else
            run_suite "BindingTests Runtime Tests" \
                bash -c 'cd BindingTests && ./run-runtime-tests.sh --skip-regen --timeout 90'
        fi
    fi
fi

exit $OVERALL_EXIT
