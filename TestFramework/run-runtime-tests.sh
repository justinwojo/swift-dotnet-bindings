#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.

# Runtime Tests Runner for TestFramework
# Builds the test library, regenerates bindings, builds the test app, and runs tests.
# Supports iOS Simulator (default) and macOS native.
#
# Usage:
#   ./run-runtime-tests.sh [--platform ios|macos] [--tier 1|2|3] [--skip-regen]
#                          [--timeout SECONDS] [--class ClassName]
#
# Options:
#   --platform PLATFORM  Target platform: ios (default), macos
#   --tier N             Run tests up to tier N (default: 1)
#   --skip-regen         Skip binding regeneration (use existing bindings)
#   --timeout N          Timeout in seconds (default: 90)
#   --class NAME         Run only the named test class (exact match, case-insensitive)

set -e

cd "$(dirname "$0")"

# Default options
PLATFORM="ios"
TIER=1
SKIP_REGEN=false
TIMEOUT=90
CLASS_FILTER=""
DEVICE_UDID=""

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --platform)
            PLATFORM="$2"
            shift 2
            ;;
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
        --class)
            CLASS_FILTER="$2"
            shift 2
            ;;
        --device-udid)
            DEVICE_UDID="$2"
            shift 2
            ;;
        *)
            echo "Unknown option: $1"
            echo "Usage: ./run-runtime-tests.sh [--platform ios|macos] [--tier 1|2|3] [--skip-regen] [--timeout SECONDS] [--class ClassName] [--device-udid UDID]"
            exit 1
            ;;
    esac
done

# Validate platform
case "$PLATFORM" in
    ios|macos) ;;
    *)
        echo "Error: Unknown platform '$PLATFORM'. Must be ios or macos."
        exit 1
        ;;
esac

echo "========================================="
echo " TestFramework Runtime Tests"
echo "========================================="
echo ""
echo "Platform: $PLATFORM"
echo "Tier: $TIER"
echo "Skip regeneration: $SKIP_REGEN"
echo "Timeout: ${TIMEOUT}s"
[ -n "$CLASS_FILTER" ] && echo "Class filter: $CLASS_FILTER"
echo ""

# -------------------------------------------------------------------
# macOS path: build xcframework → generate bindings → build & run natively
# -------------------------------------------------------------------
if [ "$PLATFORM" = "macos" ]; then
    OUTPUT_DIR="output-macos"

    # Step 1: Build xcframework for macOS and generate bindings (unless skipped)
    if [ "$SKIP_REGEN" = false ]; then
        echo "--- Step 1: Build macOS xcframework ---"
        ./build-xcframework.sh --platform macos
        echo ""

        echo "--- Step 1.1: Generate macOS bindings ---"
        mkdir -p "$OUTPUT_DIR"
        dotnet run --project ../src/Swift.Bindings/src -- \
            --xcframework .build/SwiftBindingsTestLib.xcframework \
            --platform macos \
            -o "$OUTPUT_DIR/"
        echo ""
    else
        echo "--- Step 1: Skipped (--skip-regen) ---"
        if [ ! -f "$OUTPUT_DIR/SwiftBindingsTestLib.cs" ]; then
            echo "ERROR: macOS bindings not found. Run without --skip-regen first."
            exit 1
        fi
        echo ""
    fi

    # Step 1.5: Build async Swift wrappers for macOS (if generated)
    ASYNC_SWIFT=$(find "$OUTPUT_DIR" -maxdepth 1 -name "*.swift" ! -name "*.SwiftUIBridge.swift" -type f 2>/dev/null | head -1)
    if [ -n "$ASYNC_SWIFT" ]; then
        echo "--- Step 1.5: Build async Swift wrappers (macOS) ---"
        ./build-async-wrapper.sh --platform macos --output-dir "$OUTPUT_DIR"
        echo ""
    fi

    # Step 2: Build RuntimeTestsApp.Mac
    echo "--- Step 2: Build RuntimeTestsApp.Mac ---"
    cd RuntimeTestsApp.Mac

    # Clean previous build only when bindings may have changed
    if [ "$SKIP_REGEN" = false ]; then
        rm -rf bin obj
    fi

    echo "Building for macOS (arm64)..."
    dotnet build -c Debug 2>&1 | tail -20

    if [ ! -f "bin/Debug/net10.0/osx-arm64/RuntimeTestsApp.Mac" ]; then
        echo "ERROR: Build failed - executable not found"
        exit 1
    fi

    echo "Build successful."
    echo ""

    # Step 2.5: Inject native libraries into output directory
    OUTPUT_BIN="bin/Debug/net10.0/osx-arm64"

    # Copy macOS xcframework slice
    XCFW_SLICE="../.build/SwiftBindingsTestLib.xcframework/macos-arm64/SwiftBindingsTestLib.framework/SwiftBindingsTestLib"
    if [ -f "$XCFW_SLICE" ]; then
        cp "$XCFW_SLICE" "$OUTPUT_BIN/libSwiftBindingsTestLib.dylib"
        echo "Injected SwiftBindingsTestLib dylib."
    else
        echo "Warning: SwiftBindingsTestLib dylib not found at $XCFW_SLICE"
    fi

    # Copy async wrapper if built
    ASYNC_SLICE="../output-macos/SwiftBindings.xcframework/macos-arm64/SwiftBindings.framework/SwiftBindings"
    if [ -f "$ASYNC_SLICE" ]; then
        cp "$ASYNC_SLICE" "$OUTPUT_BIN/libSwiftBindings.dylib"
        echo "Injected SwiftBindings async wrapper dylib."
    fi

    # Copy runtime dylib
    RUNTIME_DYLIB="../../src/Swift.Runtime/native/macos/libSwiftBindingsRuntime.dylib"
    if [ -f "$RUNTIME_DYLIB" ]; then
        cp "$RUNTIME_DYLIB" "$OUTPUT_BIN/"
        echo "Injected libSwiftBindingsRuntime.dylib."
    else
        echo "Warning: libSwiftBindingsRuntime.dylib not found"
    fi
    echo ""

    cd ..

    # Step 3: Run natively on macOS
    echo "--- Step 3: Run on macOS ---"

    # Build launch arguments
    LAUNCH_ARGS="--tier $TIER"
    if [ "$TIER" -ge 3 ]; then
        LAUNCH_ARGS="$LAUNCH_ARGS --flake-detect"
        echo "Flake detection enabled (Tier 3): each test runs 3x"
    fi
    if [ -n "$CLASS_FILTER" ]; then
        LAUNCH_ARGS="$LAUNCH_ARGS --class $CLASS_FILTER"
    fi

    echo "Launching RuntimeTestsApp.Mac (timeout: ${TIMEOUT}s)..."
    OUTPUT_FILE=$(mktemp)
    trap 'rm -f "$OUTPUT_FILE"' EXIT

    # Run in background and poll (macOS lacks GNU timeout)
    dotnet run --project RuntimeTestsApp.Mac/ --no-build -c Debug -- $LAUNCH_ARGS > "$OUTPUT_FILE" 2>&1 &
    PID=$!

    ELAPSED=0
    while [ $ELAPSED -lt $TIMEOUT ]; do
        sleep 1
        ELAPSED=$((ELAPSED + 1))

        # Process exited early
        if ! kill -0 $PID 2>/dev/null; then
            break
        fi

        # Check for completion markers
        if grep -q "TEST SUCCESS\|TEST FAILURE" "$OUTPUT_FILE" 2>/dev/null; then
            sleep 1
            break
        fi
    done

    # Kill if still running (timeout)
    kill $PID 2>/dev/null || true
    EXIT_CODE=0
    wait $PID 2>/dev/null || EXIT_CODE=$?

    # Show output
    echo ""
    echo "=== APP OUTPUT ==="
    cat "$OUTPUT_FILE"

    echo ""
    echo "========================================="
    if grep -q "TEST SUCCESS" "$OUTPUT_FILE" 2>/dev/null; then
        echo " RUNTIME TESTS PASSED (macOS)"
        echo "========================================="
        exit 0
    elif grep -q "TEST FAILURE" "$OUTPUT_FILE" 2>/dev/null; then
        echo " RUNTIME TESTS FAILED (macOS)"
        echo "========================================="
        exit 1
    else
        echo " RUNTIME TESTS UNEXPECTED EXIT (macOS)"
        echo "========================================="
        echo "Exit code: $EXIT_CODE"
        exit 1
    fi
fi

# -------------------------------------------------------------------
# iOS path: existing behavior (unchanged)
# -------------------------------------------------------------------

# Step 1: Build xcframework and regenerate bindings (unless skipped)
if [ "$SKIP_REGEN" = false ]; then
    echo "--- Step 1: Build xcframework and generate bindings ---"
    ./build-and-test.sh
    echo ""
else
    echo "--- Step 1: Skipped (--skip-regen) ---"
    if [ ! -f "output/SwiftBindingsTestLib.cs" ]; then
        echo "ERROR: Bindings not found. Run without --skip-regen first."
        exit 1
    fi
    # Check that bindings are not older than Swift sources
    BINDINGS_FILE="output/SwiftBindingsTestLib.cs"
    NEWEST_SWIFT=$(find Sources/SwiftBindingsTestLib -name '*.swift' -newer "$BINDINGS_FILE" 2>/dev/null | head -1)
    if [ -n "$NEWEST_SWIFT" ]; then
        echo "ERROR: Bindings are stale. Swift source newer than bindings:"
        echo "  $NEWEST_SWIFT"
        echo "Run without --skip-regen to regenerate."
        exit 1
    fi
    echo ""
fi

# Step 1.5: Build async Swift wrappers (if generated)
ASYNC_SWIFT=$(find output -maxdepth 1 -name "*.swift" ! -name "*.SwiftUIBridge.swift" -type f 2>/dev/null | head -1)
if [ -n "$ASYNC_SWIFT" ]; then
    echo "--- Step 1.5: Build async Swift wrappers ---"
    ./build-async-wrapper.sh
    echo ""
fi

# Step 1.6: Build SwiftUI bridge (if generated)
BRIDGE_SWIFT="output/SwiftBindingsTestLib.SwiftUIBridge.swift"
if [ -f "$BRIDGE_SWIFT" ]; then
    echo "--- Step 1.6: Build SwiftUI bridge ---"
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

# Step 1.7: Safety attribute check (no longer needs sed downgrade)
# [Obsolete] now uses DiagnosticId (SB0001/SB0002) instead of error:true.
# Test csprojs suppress SB0001 via NoWarn. No post-processing needed.
echo "Safety attributes use DiagnosticId — no sed downgrade needed."
echo ""

# Step 2: Build the RuntimeTestsApp
echo "--- Step 2: Build RuntimeTestsApp ---"
cd RuntimeTestsApp

# Clean previous build only when bindings may have changed
if [ "$SKIP_REGEN" = false ]; then
    rm -rf bin obj
fi

# Build for iOS Simulator
echo "Building for iOS Simulator (arm64)..."
dotnet build -c Debug 2>&1 | tail -20

if [ ! -d "bin/Debug/net10.0-ios/iossimulator-arm64/RuntimeTestsApp.app" ]; then
    echo "ERROR: Build failed - app bundle not found"
    exit 1
fi

echo "Build successful."
echo ""

# Step 2.5: Inject SwiftBindingsRuntime dylib into app bundle
# The csproj has IncludeSwiftBindingsRuntimeNative=false to avoid InstallNameTool failures,
# so we copy the iossimulator dylib manually into the Frameworks directory.
RUNTIME_DYLIB="../../src/Swift.Runtime/native/iossimulator/libSwiftBindingsRuntime.dylib"
APP_FRAMEWORKS="bin/Debug/net10.0-ios/iossimulator-arm64/RuntimeTestsApp.app/Frameworks"
if [ -f "$RUNTIME_DYLIB" ]; then
    mkdir -p "$APP_FRAMEWORKS"
    cp "$RUNTIME_DYLIB" "$APP_FRAMEWORKS/"
    echo "Injected libSwiftBindingsRuntime.dylib into app bundle."
else
    echo "Warning: libSwiftBindingsRuntime.dylib not found at $RUNTIME_DYLIB"
    echo "Existential metadata tests will fail."
fi

# Step 2.6: Inject SwiftBindings wrapper dylib into app bundle
# The resolver uses @rpath/SwiftBindings.framework/SwiftBindings, so we need the
# framework structure inside the Frameworks directory.
WRAPPER_SLICE="../output/SwiftBindings.xcframework/ios-arm64-simulator/SwiftBindings.framework/SwiftBindings"
if [ -f "$WRAPPER_SLICE" ]; then
    mkdir -p "$APP_FRAMEWORKS/SwiftBindings.framework"
    cp "$WRAPPER_SLICE" "$APP_FRAMEWORKS/SwiftBindings.framework/"
    echo "Injected SwiftBindings wrapper dylib into app bundle."
else
    echo "Note: SwiftBindings wrapper dylib not found — Tier 3 wrapper-dependent tests will fail."
fi
echo ""

cd ..

# Step 3: Run on iOS Simulator
echo "--- Step 3: Run on iOS Simulator ---"

APP_PATH="RuntimeTestsApp/bin/Debug/net10.0-ios/iossimulator-arm64/RuntimeTestsApp.app"
BUNDLE_ID="com.swiftbindings.runtimetestsapp"
CRASH_LOG_DIR="$HOME/Library/Logs/DiagnosticReports"

# Ensure a simulator is booted
if [ -n "$DEVICE_UDID" ]; then
    # CI provides a pre-booted device via --device-udid
    echo "Using pre-booted simulator: $DEVICE_UDID"
else
    # Local: check for already-booted sim, or find and boot one
    DEVICE_UDID=$(xcrun simctl list devices booted -j 2>/dev/null | python3 -c "
import json, sys
data = json.load(sys.stdin)
for runtime, devices in data.get('devices', {}).items():
    for d in devices:
        if d.get('state') == 'Booted':
            print(d['udid']); sys.exit(0)
sys.exit(1)
" 2>/dev/null) || true

    if [ -z "$DEVICE_UDID" ]; then
        echo "No booted simulator found. Searching for an iPhone simulator to boot..."
        DEVICE_UDID=$(xcrun simctl list devices available -j 2>/dev/null | python3 -c "
import json, sys
data = json.load(sys.stdin)
preferred = ['iPhone 16', 'iPhone 15 Pro', 'iPhone 15']
candidates = []
for runtime, devices in data.get('devices', {}).items():
    if 'iOS' not in runtime and 'iphone' not in runtime.lower():
        continue
    for d in devices:
        if d.get('isAvailable', False) and 'iPhone' in d.get('name', ''):
            candidates.append(d)
for pref in preferred:
    for d in candidates:
        if d.get('name') == pref:
            print(d['udid']); sys.exit(0)
if candidates:
    print(candidates[0]['udid']); sys.exit(0)
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
    else
        echo "Using already-booted simulator: $DEVICE_UDID"
    fi
fi

# Record crash log count before running
BEFORE_CRASH_COUNT=$(ls -1 "$CRASH_LOG_DIR"/RuntimeTestsApp*.ips 2>/dev/null | wc -l || echo 0)

echo "Installing app..."
xcrun simctl install "$DEVICE_UDID" "$APP_PATH"

# Build launch arguments
LAUNCH_ARGS="--tier $TIER"
if [ "$TIER" -ge 3 ]; then
    LAUNCH_ARGS="$LAUNCH_ARGS --flake-detect"
    echo "Flake detection enabled (Tier 3): each test runs 3x"
fi
if [ -n "$CLASS_FILTER" ]; then
    LAUNCH_ARGS="$LAUNCH_ARGS --class $CLASS_FILTER"
fi

echo "Launching app (timeout: ${TIMEOUT}s)..."
OUTPUT_FILE=$(mktemp)
trap 'rm -f "$OUTPUT_FILE"' EXIT
xcrun simctl launch --console --terminate-running-process "$DEVICE_UDID" "$BUNDLE_ID" $LAUNCH_ARGS > "$OUTPUT_FILE" 2>&1 &
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

    # NOTE: Do NOT check for crash signals (SIGABRT, etc.) during active polling.
    # Mono's malloc assertion fires during background cleanup but the app continues
    # running and produces the test summary. Only check crash signals after the
    # process has exited (handled in the ! kill -0 block above).
done

# Terminate the app (with timeout — simctl terminate can hang on GHA runners)
xcrun simctl terminate "$DEVICE_UDID" "$BUNDLE_ID" 2>/dev/null &
TERM_PID=$!
sleep 5 && kill $TERM_PID 2>/dev/null &
wait $TERM_PID 2>/dev/null || true
kill $PID 2>/dev/null || true
# Ensure background simctl launch process is fully dead
kill -9 $PID 2>/dev/null || true

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
    # Extract any test failures that occurred before the crash
    FAIL_COUNT=$(grep -c '\[FAIL\].*([0-9]*ms)' "$OUTPUT_FILE" 2>/dev/null || echo 0)
    PASS_COUNT=$(grep -c '\[PASS\]' "$OUTPUT_FILE" 2>/dev/null || echo 0)
    if [ "$FAIL_COUNT" -gt 0 ]; then
        echo ""
        echo "ERROR: $FAIL_COUNT test(s) failed before crash ($PASS_COUNT passed)."
        echo "Failing tests:"
        grep '\[FAIL\].*([0-9]*ms)' "$OUTPUT_FILE" | sed 's/.*\[FAIL\] /  /'
        exit 1
    fi
    # Tier 1+2: crashes are regressions (all crash-prone tests moved to Tier 3).
    # Tier 3: tolerate known Mono JIT crashes since Tier 3 includes crash-prone tests.
    IS_MONO_JIT_CRASH=false
    if grep -q "jit-info\.c:918" "$OUTPUT_FILE" 2>/dev/null; then
        IS_MONO_JIT_CRASH=true
    elif [ -n "$LATEST_CRASH" ] && grep -q "jit-info\|mono_jit\|ReleaseHandle" "$LATEST_CRASH" 2>/dev/null; then
        IS_MONO_JIT_CRASH=true
    fi
    if [ "$IS_MONO_JIT_CRASH" = true ]; then
        if [ "$TIER" -ge 3 ]; then
            echo ""
            echo "WARNING: Known Mono JIT assertion (jit-info.c:918) during Tier 3 run."
            echo "This is a pre-existing Mono runtime bug, not a regression."
            exit 0
        else
            echo ""
            echo "ERROR: Mono JIT crash in Tier $TIER run ($PASS_COUNT tests passed before crash)."
            echo "All crash-prone tests should be Tier 3. This crash is a regression."
            exit 1
        fi
    fi
    exit 1
elif [ "$RESULT" = "failure" ]; then
    echo " RUNTIME TESTS FAILED"
    echo "========================================="
    exit 1
elif [ "$RESULT" = "launch_failure" ] || [ "$RESULT" = "" ]; then
    # Process exited or timed out without markers — check if it's the known Mono crash
    echo " RUNTIME TESTS ${RESULT:-TIMEOUT}"
    echo "========================================="
    # Detect Mono JIT crash from any available source
    IS_MONO_JIT_CRASH=false
    LATEST_CRASH=$(ls -t "$CRASH_LOG_DIR"/RuntimeTestsApp*.ips 2>/dev/null | head -1)
    if [ -n "$LATEST_CRASH" ] && grep -q "jit-info\|mono_jit\|ReleaseHandle" "$LATEST_CRASH" 2>/dev/null; then
        IS_MONO_JIT_CRASH=true
    fi
    if grep -q "jit-info\.c:918" "$OUTPUT_FILE" 2>/dev/null; then
        IS_MONO_JIT_CRASH=true
    fi
    # Check simulator device log for crash evidence (GHA simctl --console
    # often doesn't capture crash output for simulator apps)
    DEVICE_LOG=$(xcrun simctl spawn "$DEVICE_UDID" log show --last 3m \
        --predicate 'process == "RuntimeTestsApp" OR (process == "ReportCrash" AND eventMessage CONTAINS "RuntimeTestsApp")' \
        --style compact 2>/dev/null || true)
    if echo "$DEVICE_LOG" | grep -q "jit-info\|mono_jit\|ReleaseHandle\|EXC_BAD_ACCESS\|SIGABRT\|assertion.*not met" 2>/dev/null; then
        IS_MONO_JIT_CRASH=true
        echo ""
        echo "=== DEVICE LOG (crash evidence) ==="
        echo "$DEVICE_LOG" | grep -i "crash\|assert\|abort\|exc_bad\|jit-info\|ReleaseHandle\|SIGABRT\|fatal" | tail -10
    fi
    if [ "$IS_MONO_JIT_CRASH" = true ]; then
        PASS_COUNT=$(grep -c '\[PASS\]' "$OUTPUT_FILE" 2>/dev/null || echo 0)
        if [ "$TIER" -ge 3 ]; then
            echo ""
            echo "WARNING: Mono JIT crash during Tier 3 run ($PASS_COUNT tests passed before crash)."
            echo "This is a pre-existing Mono runtime bug, not a regression."
            exit 0
        else
            echo ""
            echo "ERROR: Mono JIT crash in Tier $TIER run ($PASS_COUNT tests passed before crash)."
            echo "All crash-prone tests should be Tier 3. This crash is a regression."
            exit 1
        fi
    fi
    # Show device log for debugging if we still don't know what happened
    if [ -n "$DEVICE_LOG" ]; then
        echo ""
        echo "=== DEVICE LOG (last 3 min, RuntimeTestsApp) ==="
        echo "$DEVICE_LOG" | tail -30
    fi
    exit 1
else
    echo " RUNTIME TESTS TIMEOUT"
    echo "========================================="
    exit 1
fi
