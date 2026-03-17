#!/bin/bash
# Run all tests with proper dotnet host path workaround

set -e

cd "$(dirname "$0")"

DOTNET_PATH=$(which dotnet)

# Build everything first
echo "=== Building Projects ==="
./build.sh

echo ""
echo "=== Running Unit Tests ==="
dotnet test src/Swift.Bindings/tests/UnitTests --no-build -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

echo ""
echo "=== Running Runtime Tests ==="
dotnet test src/Swift.Runtime/tests --no-build -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

echo ""
echo "=== Running Analyzer Tests ==="
dotnet test src/Swift.Analyzers.Tests --no-build -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

echo ""
echo "=== Running BindingTests Regression Suite ==="
if [ "$(uname)" != "Darwin" ]; then
    echo "Skipping BindingTests (requires macOS with Xcode)."
elif [ ! -d "BindingTests" ]; then
    echo "BindingTests directory not found, skipping."
else
    cd BindingTests
    ./build-and-test.sh --strict
    ./generate-coverage-report.sh
    ./check-baselines.sh
    cd ..

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
            echo "ERROR: $DEGRADED must-pass feature(s) are degraded in BindingTests."
            echo "See $COVERAGE_JSON for details."
            exit 1
        fi
    fi

    # Run runtime tests on iOS Simulator (if available)
    echo ""
    echo "=== Running BindingTests Runtime Tests ==="
    if ! command -v xcrun &>/dev/null; then
        echo "Skipping runtime tests (xcrun not available)."
    elif ! xcrun simctl list devices &>/dev/null 2>&1; then
        echo "Skipping runtime tests (iOS Simulator runtime not available)."
    else
        # Check for an available iPhone simulator
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
            echo "Skipping runtime tests (no available iPhone simulator found)."
        else
            cd BindingTests
            # Simulator mode: runs all tests except [MonoJitCrash] and [Skip].
            # Any failure or crash is a regression.
            ./run-runtime-tests.sh --skip-regen --timeout 90
            cd ..
        fi
    fi
fi
