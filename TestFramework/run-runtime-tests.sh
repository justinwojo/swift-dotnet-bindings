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
    echo ""
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

# Find booted simulator
SIMULATOR_ID=$(xcrun simctl list devices booted -j | python3 -c "import sys, json; devices = json.load(sys.stdin)['devices']; booted = [d['udid'] for v in devices.values() for d in v if d['state'] == 'Booted']; print(booted[0] if booted else '')" 2>/dev/null || echo "")

if [ -z "$SIMULATOR_ID" ]; then
    echo "No booted simulator found. Booting iPhone 16 Pro..."
    SIMULATOR_ID=$(xcrun simctl list devices available -j | python3 -c "import sys, json; devices = json.load(sys.stdin)['devices']; iphones = [d['udid'] for v in devices.values() for d in v if 'iPhone 16' in d['name'] or 'iPhone 15' in d['name']]; print(iphones[0] if iphones else '')" 2>/dev/null || echo "")

    if [ -z "$SIMULATOR_ID" ]; then
        echo "ERROR: No suitable simulator found"
        exit 1
    fi

    xcrun simctl boot "$SIMULATOR_ID" 2>/dev/null || true
    sleep 3
fi

echo "Using simulator: $SIMULATOR_ID"

# Install and launch the app
APP_PATH="RuntimeTestsApp/bin/Debug/net10.0-ios/iossimulator-arm64/RuntimeTestsApp.app"

echo "Installing app..."
xcrun simctl install "$SIMULATOR_ID" "$APP_PATH"

echo "Launching app..."
xcrun simctl launch --console-pty "$SIMULATOR_ID" com.swiftbindings.runtimetestsapp --tier "$TIER" &
LAUNCH_PID=$!

# Monitor for test completion
echo ""
echo "Waiting for test completion (timeout: ${TIMEOUT}s)..."
echo ""

START_TIME=$(date +%s)
SUCCESS=false
FAILURE=false

# Create a named pipe for output
PIPE=$(mktemp -u)
mkfifo "$PIPE"

# Capture output in background
(xcrun simctl spawn "$SIMULATOR_ID" log stream --predicate 'subsystem == "com.apple.console"' 2>/dev/null | while read -r line; do
    echo "$line"
    if echo "$line" | grep -q "TEST SUCCESS"; then
        touch /tmp/runtime_test_success
    fi
    if echo "$line" | grep -q "TEST FAILURE"; then
        touch /tmp/runtime_test_failure
    fi
done) &
LOG_PID=$!

# Clean up markers
rm -f /tmp/runtime_test_success /tmp/runtime_test_failure

# Wait for result or timeout
while true; do
    CURRENT_TIME=$(date +%s)
    ELAPSED=$((CURRENT_TIME - START_TIME))

    if [ -f /tmp/runtime_test_success ]; then
        SUCCESS=true
        break
    fi

    if [ -f /tmp/runtime_test_failure ]; then
        FAILURE=true
        break
    fi

    if [ $ELAPSED -ge $TIMEOUT ]; then
        echo ""
        echo "=== TIMEOUT ==="
        break
    fi

    sleep 1
done

# Cleanup
kill $LOG_PID 2>/dev/null || true
kill $LAUNCH_PID 2>/dev/null || true
rm -f "$PIPE" /tmp/runtime_test_success /tmp/runtime_test_failure

echo ""
echo "========================================="
if [ "$SUCCESS" = true ]; then
    echo " RUNTIME TESTS PASSED"
    echo "========================================="
    exit 0
elif [ "$FAILURE" = true ]; then
    echo " RUNTIME TESTS FAILED"
    echo "========================================="
    exit 1
else
    echo " RUNTIME TESTS TIMEOUT"
    echo "========================================="
    exit 1
fi
