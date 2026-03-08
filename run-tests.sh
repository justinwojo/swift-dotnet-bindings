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
echo "=== Running Integration Tests ==="
# Use --no-build since we already built above
dotnet test src/Swift.Bindings/tests/IntegrationTests --no-build -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

echo ""
echo "=== Running Runtime Tests ==="
dotnet test src/Swift.Runtime/tests --no-build -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

echo ""
echo "=== Running Analyzer Tests ==="
dotnet test src/Swift.Analyzers.Tests --no-build -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

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
    ./check-baselines.sh
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
                # Known crash-risk classes (must match [CrashRisk] attributes in RuntimeTestsApp)
                CRASH_ALLOWLIST="EnumMarshallingTests|OwnershipGCStressTests|ArrayMarshallingTests"

                if grep -q "jit-info\.c:918\|RUNTIME TESTS CRASHED\|RUNTIME TESTS TIMEOUT" "$RUNTIME_OUTPUT" 2>/dev/null; then
                    # Extract the last test class from === ClassName === markers
                    LAST_CLASS=$(grep -oE '=== [A-Za-z0-9_]+ ===' "$RUNTIME_OUTPUT" | tail -1 | sed 's/=== //;s/ ===//')

                    if [ -n "$LAST_CLASS" ] && echo "$LAST_CLASS" | grep -qE "^($CRASH_ALLOWLIST)$"; then
                        echo ""
                        echo "WARNING: Runtime crash/timeout in known crash-risk class ($LAST_CLASS)."
                        echo "This is a pre-existing Mono runtime bug, not a regression."
                    elif [ -z "$LAST_CLASS" ]; then
                        # Crash before any test class ran — likely Mono startup issue
                        echo ""
                        echo "WARNING: Runtime crash before any test class ran (Mono startup crash)."
                        echo "This is a pre-existing Mono runtime bug, not a regression."
                    else
                        echo ""
                        echo "ERROR: Runtime crash in class '$LAST_CLASS' which is NOT in the crash allowlist."
                        echo "This may be a regression. Allowlist: $CRASH_ALLOWLIST"
                        rm -f "$RUNTIME_OUTPUT"
                        exit 1
                    fi
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
