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
    ./build-and-test.sh
    ./generate-coverage-report.sh
    cd ..

    # Check for degraded must-pass features
    COVERAGE_JSON="TestFramework/output/coverage-matrix.json"
    if [ -f "$COVERAGE_JSON" ]; then
        DEGRADED=$(python3 -c "
import json, sys
with open('$COVERAGE_JSON') as f:
    data = json.load(f)
mp = data.get('summary', {}).get('must_pass', {})
degraded = mp.get('degraded', 0)
missing = mp.get('missing', 0)
print(degraded + missing)
" 2>/dev/null || echo "0")
        if [ "$DEGRADED" -gt 0 ]; then
            echo ""
            echo "WARNING: $DEGRADED must-pass feature(s) are degraded or missing in TestFramework."
            echo "See $COVERAGE_JSON for details."
        fi
    fi
fi
