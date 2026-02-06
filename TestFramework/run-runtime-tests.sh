#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.

# Runtime Tests Runner for TestFramework
# Builds the test library, regenerates bindings, builds the test app, and runs on iOS Simulator.
#
# Usage:
#   ./run-runtime-tests.sh [--tier 1|2|3] [--skip-regen] [--timeout SECONDS]
#
# Options:
#   --tier N       Run tests up to tier N (default: 1)
#   --skip-regen   Skip binding regeneration (use existing bindings)
#   --timeout N    Timeout in seconds (default: 60)

set -e

cd "$(dirname "$0")"

# Default options
TIER=1
SKIP_REGEN=false
TIMEOUT=60

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --tier)
            TIER="$2"
            shift 2
            ;;
        --skip-regen)
            SKIP_REGEN=true
            shift
            ;;
        --timeout)
            TIMEOUT="$2"
            shift 2
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

echo "========================================="
echo " TestFramework Runtime Tests"
echo "========================================="
echo ""
echo "Tier: $TIER"
echo "Skip regeneration: $SKIP_REGEN"
echo "Timeout: ${TIMEOUT}s"
echo ""

# Step 1: Build xcframework and regenerate bindings (unless skipped)
if [ "$SKIP_REGEN" = false ]; then
    echo "--- Step 1: Build xcframework and generate bindings ---"
    ./build-and-test.sh
    echo ""
else
    echo "--- Step 1: Skipped (--skip-regen) ---"
    if [ ! -f "output/Swift.SwiftBindingsTestLib.cs" ]; then
        echo "ERROR: Bindings not found. Run without --skip-regen first."
        exit 1
    fi
    # Check that bindings are not older than Swift sources
    BINDINGS_FILE="output/Swift.SwiftBindingsTestLib.cs"
    NEWEST_SWIFT=$(find Sources/SwiftBindingsTestLib -name '*.swift' -newer "$BINDINGS_FILE" 2>/dev/null | head -1)
    if [ -n "$NEWEST_SWIFT" ]; then
        echo "ERROR: Bindings are stale. Swift source newer than bindings:"
        echo "  $NEWEST_SWIFT"
        echo "Run without --skip-regen to regenerate."
        exit 1
    fi
    echo ""
fi

# Step 1.5: Build SwiftUI bridge (if generated)
BRIDGE_SWIFT="output/Swift.SwiftBindingsTestLib.SwiftUIBridge.swift"
if [ -f "$BRIDGE_SWIFT" ]; then
    echo "--- Step 1.5: Build SwiftUI bridge ---"
    ./build-bridge.sh
    echo ""
elif [ "$SKIP_REGEN" = false ]; then
    # SwiftUI sources exist in the test library — bridge must be generated
    if [ -d "Sources/SwiftBindingsTestLib/SwiftUI" ]; then
        echo "ERROR: Bridge file not generated after binding generation."
        echo "SwiftUI sources exist but no bridge was emitted. Check generator output."
        exit 1
    else
        echo "Note: No SwiftUI sources present, bridge generation skipped."
        echo ""
    fi
fi

# Step 2: Build the RuntimeTestsApp
echo "--- Step 2: Build RuntimeTestsApp ---"
cd RuntimeTestsApp

# Clean previous build
rm -rf bin obj

# Build for iOS Simulator
echo "Building for iOS Simulator (arm64)..."
dotnet build -c Debug 2>&1 | tail -20

if [ ! -d "bin/Debug/net10.0-ios/iossimulator-arm64/RuntimeTestsApp.app" ]; then
    echo "ERROR: Build failed - app bundle not found"
    exit 1
fi

echo "Build successful."
echo ""

cd ..

# Step 3: Run on iOS Simulator
echo "--- Step 3: Run on iOS Simulator ---"

APP_PATH="RuntimeTestsApp/bin/Debug/net10.0-ios/iossimulator-arm64/RuntimeTestsApp.app"
BUNDLE_ID="com.swiftbindings.runtimetestsapp"
CRASH_LOG_DIR="$HOME/Library/Logs/DiagnosticReports"

# Ensure a simulator is booted
BOOTED_UDID=$(xcrun simctl list devices booted -j 2>/dev/null | python3 -c "
import json, sys
data = json.load(sys.stdin)
for runtime, devices in data.get('devices', {}).items():
    for d in devices:
        if d.get('state') == 'Booted':
            print(d['udid']); sys.exit(0)
sys.exit(1)
" 2>/dev/null) || true

if [ -z "$BOOTED_UDID" ]; then
    echo "No booted simulator found. Searching for an iPhone simulator to boot..."
    DEVICE_UDID=$(xcrun simctl list devices available -j 2>/dev/null | python3 -c "
import json, sys
data = json.load(sys.stdin)
for runtime, devices in data.get('devices', {}).items():
    if 'iOS' not in runtime and 'iphone' not in runtime.lower():
        continue
    for d in devices:
        if d.get('isAvailable', False) and 'iPhone' in d.get('name', ''):
            print(d['udid']); sys.exit(0)
sys.exit(1)
" 2>/dev/null) || true

    if [ -z "$DEVICE_UDID" ]; then
        echo "ERROR: No available iPhone simulator found. Install one via Xcode."
        exit 1
    fi

    echo "Booting simulator $DEVICE_UDID..."
    xcrun simctl boot "$DEVICE_UDID"
    echo "Waiting for simulator to finish booting..."
    xcrun simctl bootstatus "$DEVICE_UDID" -b
    echo "Simulator booted."
fi

# Record crash log count before running
BEFORE_CRASH_COUNT=$(ls -1 "$CRASH_LOG_DIR"/RuntimeTestsApp*.ips 2>/dev/null | wc -l || echo 0)

echo "Installing app..."
xcrun simctl install booted "$APP_PATH"

# Build launch arguments
LAUNCH_ARGS="--tier $TIER"
if [ "$TIER" -ge 3 ]; then
    LAUNCH_ARGS="$LAUNCH_ARGS --flake-detect"
    echo "Flake detection enabled (Tier 3): each test runs 3x"
fi

echo "Launching app (timeout: ${TIMEOUT}s)..."
OUTPUT_FILE=$(mktemp)
xcrun simctl launch --console --terminate-running-process booted "$BUNDLE_ID" $LAUNCH_ARGS > "$OUTPUT_FILE" 2>&1 &
PID=$!

# Poll for success, failure, or crash markers
ELAPSED=0
RESULT=""
while [ $ELAPSED -lt $TIMEOUT ]; do
    sleep 1
    ELAPSED=$((ELAPSED + 1))

    # P2 fix: detect early launch failure (process exited without producing test output)
    if ! kill -0 $PID 2>/dev/null; then
        # Launch process exited — check if we got a result marker
        if grep -q "TEST SUCCESS" "$OUTPUT_FILE" 2>/dev/null; then
            RESULT="success"
        elif grep -q "TEST FAILURE" "$OUTPUT_FILE" 2>/dev/null; then
            RESULT="failure"
        elif grep -q "SIGABRT\|SIGSEGV\|SIGBUS\|Fatal error\|CRASH\|EXC_BAD_ACCESS\|Assertion.*not met" "$OUTPUT_FILE" 2>/dev/null; then
            RESULT="crash"
        else
            RESULT="launch_failure"
        fi
        break
    fi

    if grep -q "TEST SUCCESS" "$OUTPUT_FILE" 2>/dev/null; then
        RESULT="success"
        break
    fi

    if grep -q "TEST FAILURE" "$OUTPUT_FILE" 2>/dev/null; then
        RESULT="failure"
        break
    fi

    # Detect crashes via signal markers in output
    if grep -q "SIGABRT\|SIGSEGV\|SIGBUS\|Fatal error\|CRASH\|EXC_BAD_ACCESS\|Assertion.*not met" "$OUTPUT_FILE" 2>/dev/null; then
        RESULT="crash"
        break
    fi
done

# Terminate the app
xcrun simctl terminate booted "$BUNDLE_ID" 2>/dev/null || true
kill $PID 2>/dev/null || true

# Check for new crash logs (catches crashes the output markers might miss)
if [ "$RESULT" != "success" ] && [ "$RESULT" != "failure" ]; then
    AFTER_CRASH_COUNT=$(ls -1 "$CRASH_LOG_DIR"/RuntimeTestsApp*.ips 2>/dev/null | wc -l || echo 0)
    if [ "$AFTER_CRASH_COUNT" -gt "$BEFORE_CRASH_COUNT" ]; then
        RESULT="crash"
    fi
fi

# Show output
echo ""
echo "=== APP OUTPUT ==="
cat "$OUTPUT_FILE"
rm -f "$OUTPUT_FILE"

echo ""
echo "========================================="
if [ "$RESULT" = "success" ]; then
    echo " RUNTIME TESTS PASSED"
    echo "========================================="
    exit 0
elif [ "$RESULT" = "crash" ]; then
    echo " RUNTIME TESTS CRASHED"
    echo "========================================="
    # Show latest crash log if available
    LATEST_CRASH=$(ls -t "$CRASH_LOG_DIR"/RuntimeTestsApp*.ips 2>/dev/null | head -1)
    if [ -n "$LATEST_CRASH" ]; then
        echo "Crash log: $LATEST_CRASH"
        head -30 "$LATEST_CRASH"
    fi
    exit 1
elif [ "$RESULT" = "failure" ]; then
    echo " RUNTIME TESTS FAILED"
    echo "========================================="
    exit 1
elif [ "$RESULT" = "launch_failure" ]; then
    echo " RUNTIME TESTS LAUNCH FAILURE"
    echo "========================================="
    echo "The app process exited without producing test output."
    echo "Check that the simulator is running and the app bundle is valid."
    exit 1
else
    echo " RUNTIME TESTS TIMEOUT"
    echo "========================================="
    exit 1
fi
