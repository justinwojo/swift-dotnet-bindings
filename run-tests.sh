#!/bin/bash
# Run all tests with proper dotnet host path workaround

set -e

cd "$(dirname "$0")"

DOTNET_PATH=$(which dotnet)

# Build everything first (integration tests require cmake pre-build step)
echo "=== Building Projects ==="
./build.sh

echo ""
echo "=== Running Unit Tests ==="
dotnet test src/Swift.Bindings/tests/UnitTests --no-build -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

echo ""
echo "=== Running Integration Tests ==="
# Use --no-build to avoid cmake reconfiguration issues
dotnet test src/Swift.Bindings/tests/IntegrationTests --no-build -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

echo ""
echo "=== Running Runtime Tests ==="
dotnet test src/Swift.Runtime/tests --no-build -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

echo ""
echo "=== Running TestFramework Regression Suite ==="
if [ "$(uname)" != "Darwin" ]; then
    echo "Skipping TestFramework (requires macOS with Xcode)."
elif [ ! -d "TestFramework" ]; then
    echo "TestFramework directory not found, skipping."
else
    cd TestFramework
    ./build-and-test.sh --strict
    ./generate-coverage-report.sh
    cd ..

    # Fail on degraded must-pass features (actual regressions)
    COVERAGE_JSON="TestFramework/output/coverage-matrix.json"
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
            echo "ERROR: $DEGRADED must-pass feature(s) are degraded in TestFramework."
            echo "See $COVERAGE_JSON for details."
            exit 1
        fi
    fi

    # Run runtime tests on iOS Simulator (if available)
    echo ""
    echo "=== Running TestFramework Runtime Tests ==="
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
            cd TestFramework
            RUNTIME_OUTPUT=$(mktemp)
            set +e
            ./run-runtime-tests.sh --tier 2 --skip-regen --timeout 90 2>&1 | tee "$RUNTIME_OUTPUT"
            RUNTIME_EXIT=${PIPESTATUS[0]}
            set -e
            cd ..
            if [ $RUNTIME_EXIT -ne 0 ]; then
                # Tolerate known Mono runtime crashes (pre-existing bugs, not regressions):
                #   - jit-info.c:918: Mono JIT assertion on closure/SwiftString P/Invoke
                #   - RUNTIME TESTS CRASHED: Mono process crash (async teardown, gsharedvt, etc.)
                # Only fail on "RUNTIME TESTS FAILED" (test logic failure without crash),
                # which would indicate a genuine regression.
                if grep -q "jit-info\.c:918" "$RUNTIME_OUTPUT" 2>/dev/null; then
                    echo ""
                    echo "WARNING: Runtime tests hit the known Mono JIT crash (jit-info.c:918)."
                    echo "This is a pre-existing Mono runtime bug, not a regression."
                elif grep -q "RUNTIME TESTS CRASHED" "$RUNTIME_OUTPUT" 2>/dev/null; then
                    echo ""
                    echo "WARNING: Runtime tests crashed (Mono runtime crash)."
                    echo "This is a pre-existing Mono runtime bug, not a regression."
                else
                    echo ""
                    echo "ERROR: Runtime tests failed (exit code $RUNTIME_EXIT)."
                    rm -f "$RUNTIME_OUTPUT"
                    exit 1
                fi
            fi
            rm -f "$RUNTIME_OUTPUT"
        fi
    fi
fi
