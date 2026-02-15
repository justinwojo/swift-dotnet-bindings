#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# NativeAOT Device Test Runner
# Builds, publishes, deploys, and runs NativeAOT tests on a physical iOS device.
#
# Prerequisites:
#   - Apple Developer certificate + provisioning profile (code signing)
#   - iPhone connected via USB or Wi-Fi
#   - TestFramework bindings generated (run build-and-test.sh first)
#   - Device xcframeworks built (run build-xcframework.sh --include-device)
#
# Usage:
#   ./run-nativeaot-device-tests.sh [--skip-build] [--skip-publish] [--device UDID]
#
# Options:
#   --skip-build     Skip xcframework + wrapper rebuild
#   --skip-publish   Skip dotnet publish (use existing .app)
#   --device UDID    Target specific device (default: first connected)
#   --timeout N      Per-launch timeout in seconds (default: 60)

set -e
cd "$(dirname "$0")"

# Defaults
SKIP_BUILD=false
SKIP_PUBLISH=false
DEVICE_UDID=""
TIMEOUT=60

while [[ $# -gt 0 ]]; do
    case $1 in
        --skip-build)   SKIP_BUILD=true; shift ;;
        --skip-publish) SKIP_PUBLISH=true; shift ;;
        --device)       DEVICE_UDID="$2"; shift 2 ;;
        --timeout)      TIMEOUT="$2"; shift 2 ;;
        *)
            echo "Unknown option: $1"
            echo "Usage: ./run-nativeaot-device-tests.sh [--skip-build] [--skip-publish] [--device UDID] [--timeout N]"
            exit 1
            ;;
    esac
done

BUNDLE_ID="com.swiftbindings.nativeaot.device"
PROJECT_DIR="NativeAotTestApp.Device"
PROJECT_FILE="$PROJECT_DIR/NativeAotTestApp.Device.csproj"

echo "========================================="
echo " NativeAOT Device Test Runner"
echo "========================================="
echo ""

# --- Step 0: Find device ---
echo "--- Step 0: Find connected device ---"
if [ -z "$DEVICE_UDID" ]; then
    DEVICE_UDID=$(xcrun devicectl list devices 2>/dev/null | grep -i "iphone\|ipad" | head -1 | awk '{print $NF}' || true)
    if [ -z "$DEVICE_UDID" ]; then
        # Try the xctrace approach
        DEVICE_UDID=$(xcrun xctrace list devices 2>/dev/null | grep -v "Simulator" | grep "(.*)" | head -1 | sed 's/.*(\(.*\))/\1/' || true)
    fi
fi

if [ -z "$DEVICE_UDID" ]; then
    echo "ERROR: No connected iOS device found."
    echo "Connect your iPhone and try again, or use --device UDID."
    exit 1
fi
echo "Device: $DEVICE_UDID"
echo ""

# --- Step 1: Build device xcframeworks ---
if [ "$SKIP_BUILD" = false ]; then
    echo "--- Step 1: Build device xcframeworks ---"

    echo "Building SwiftBindingsTestLib with device slice..."
    ./build-xcframework.sh --include-device 2>&1 | tail -5
    echo ""

    echo "Building SwiftBindings wrapper with device slice..."
    ./build-wrapper-device.sh 2>&1 | tail -5
    echo ""
else
    echo "--- Step 1: Skipped (--skip-build) ---"

    # Verify device slices exist
    if [ ! -d ".build/SwiftBindingsTestLib.xcframework/ios-arm64" ]; then
        echo "ERROR: Device slice missing from SwiftBindingsTestLib.xcframework"
        echo "Run without --skip-build first."
        exit 1
    fi
    if [ ! -d "output/SwiftBindings.xcframework/ios-arm64" ]; then
        echo "ERROR: Device slice missing from SwiftBindings.xcframework"
        echo "Run without --skip-build first."
        exit 1
    fi
    echo ""
fi

# --- Step 1.5: Safety attribute check ---
echo "--- Step 1.5: Safety attributes use DiagnosticId (no downgrade needed) ---"
echo "Safety attributes use DiagnosticId (SB0001/SB0002) — no sed downgrade needed."
echo ""

# --- Step 2: Publish NativeAOT for device ---
if [ "$SKIP_PUBLISH" = false ]; then
    echo "--- Step 2: Publish NativeAOT for ios-arm64 ---"
    echo "This may take several minutes (ILCompiler + code signing)..."
    mkdir -p logs

    cd "$PROJECT_DIR"
    rm -rf bin obj
    if dotnet publish -c Release 2>&1 | tee ../logs/nativeaot-device-publish.log | tail -20; then
        echo ""
        echo "Publish succeeded."
    else
        echo ""
        echo "ERROR: Publish failed. See logs/nativeaot-device-publish.log"
        echo ""
        echo "Last 40 lines:"
        tail -40 ../logs/nativeaot-device-publish.log
        exit 1
    fi
    cd ..
    echo ""
else
    echo "--- Step 2: Skipped (--skip-publish) ---"
    echo ""
fi

# --- Step 3: Locate .app bundle ---
echo "--- Step 3: Locate app bundle ---"
APP_PATH=$(find "$PROJECT_DIR/bin" -name "NativeAotTestApp.Device.app" -type d 2>/dev/null | head -1)
if [ -z "$APP_PATH" ]; then
    echo "ERROR: App bundle not found. Run without --skip-publish."
    exit 1
fi
echo "App bundle: $APP_PATH"
APP_SIZE=$(du -sh "$APP_PATH" 2>/dev/null | cut -f1)
echo "App size: $APP_SIZE"
echo ""

# --- Step 4: Install on device ---
echo "--- Step 4: Install on device ---"
xcrun devicectl device install app --device "$DEVICE_UDID" "$APP_PATH" 2>&1
echo ""

# --- Step 5: Run tests ---
echo "--- Step 5: Run tests ---"
echo "Launching app with --test-id all (single launch, all tests)..."
echo ""

# Launch with --console to capture stdout
OUTPUT_FILE=$(mktemp)
xcrun devicectl device process launch \
    --device "$DEVICE_UDID" \
    --console \
    "$BUNDLE_ID" \
    -- --test-id all \
    > "$OUTPUT_FILE" 2>&1 &
LAUNCH_PID=$!

# Wait with timeout
ELAPSED=0
while kill -0 $LAUNCH_PID 2>/dev/null && [ $ELAPSED -lt $TIMEOUT ]; do
    sleep 1
    ELAPSED=$((ELAPSED + 1))

    # Check for completion marker
    if grep -q "ALL TESTS COMPLETE" "$OUTPUT_FILE" 2>/dev/null; then
        sleep 2
        break
    fi
done

# Kill if still running
kill $LAUNCH_PID 2>/dev/null || true
wait $LAUNCH_PID 2>/dev/null || true

# Terminate app on device
xcrun devicectl device process terminate --device "$DEVICE_UDID" "$BUNDLE_ID" 2>/dev/null || true

# --- Step 6: Parse results ---
echo ""
echo "========================================="
echo " NativeAOT Device Test Results"
echo "========================================="
echo ""

if [ -f "$OUTPUT_FILE" ]; then
    cat "$OUTPUT_FILE"
    echo ""

    PASS_COUNT=$(grep -c "^PASS:" "$OUTPUT_FILE" 2>/dev/null || echo 0)
    FAIL_COUNT=$(grep -c "^FAIL:" "$OUTPUT_FILE" 2>/dev/null || echo 0)

    echo "-----------------------------------------"
    echo "  Passed: $PASS_COUNT"
    echo "  Failed: $FAIL_COUNT"
    echo "-----------------------------------------"

    if [ $ELAPSED -ge $TIMEOUT ] && ! grep -q "ALL TESTS COMPLETE" "$OUTPUT_FILE" 2>/dev/null; then
        echo "  WARNING: Timed out after ${TIMEOUT}s"
    fi
else
    echo "ERROR: No output captured."
fi

# Save output log
cp "$OUTPUT_FILE" logs/nativeaot-device-results.log 2>/dev/null || true
rm -f "$OUTPUT_FILE"
echo ""
echo "Results saved to logs/nativeaot-device-results.log"
echo "Publish log: logs/nativeaot-device-publish.log"
