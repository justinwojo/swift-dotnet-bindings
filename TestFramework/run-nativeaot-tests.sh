#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.

# NativeAOT Test Runner for TestFramework
# Publishes NativeAOT test apps and runs each test as a separate simulator launch.
#
# Usage:
#   ./run-nativeaot-tests.sh [--timeout SECONDS] [--test-id ID] [--no-inject] [--skip-publish]
#
# Options:
#   --timeout N       Per-test timeout in seconds (default: 30)
#   --test-id ID      Run only the named test (skip all others)
#   --no-inject       Skip dylib injection (for n2-resolve-no-inject test)
#   --skip-publish    Skip dotnet publish (use existing app bundles)

set -e

cd "$(dirname "$0")"

# Default options
PER_TEST_TIMEOUT=30
TEST_FILTER=""
NO_INJECT=false
SKIP_PUBLISH=false

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --timeout)
            PER_TEST_TIMEOUT="$2"
            shift 2
            ;;
        --test-id)
            TEST_FILTER="$2"
            shift 2
            ;;
        --no-inject)
            NO_INJECT=true
            shift
            ;;
        --skip-publish)
            SKIP_PUBLISH=true
            shift
            ;;
        *)
            echo "Unknown option: $1"
            echo "Usage: ./run-nativeaot-tests.sh [--timeout N] [--test-id ID] [--no-inject] [--skip-publish]"
            exit 1
            ;;
    esac
done

echo "========================================="
echo " NativeAOT Test Runner"
echo "========================================="
echo ""
echo "Per-test timeout: ${PER_TEST_TIMEOUT}s"
[ -n "$TEST_FILTER" ] && echo "Test filter: $TEST_FILTER"
[ "$NO_INJECT" = true ] && echo "No-inject mode: yes"
[ "$SKIP_PUBLISH" = true ] && echo "Skip publish: yes"
echo ""

# Results tracking
declare -a RESULTS=()
PASS_COUNT=0
FAIL_COUNT=0
CRASH_COUNT=0
COMPILE_FAIL_COUNT=0

record_result() {
    local status="$1"
    local test_id="$2"
    local detail="$3"

    if [ -n "$detail" ]; then
        RESULTS+=("$status: $test_id ($detail)")
    else
        RESULTS+=("$status: $test_id")
    fi

    case "$status" in
        PASS) PASS_COUNT=$((PASS_COUNT + 1)) ;;
        FAIL) FAIL_COUNT=$((FAIL_COUNT + 1)) ;;
        CRASH) CRASH_COUNT=$((CRASH_COUNT + 1)) ;;
        COMPILE_FAIL) COMPILE_FAIL_COUNT=$((COMPILE_FAIL_COUNT + 1)) ;;
    esac
}

# --- Step 0: Staleness check ---
echo "--- Step 0: Staleness check ---"
BINDINGS_FILE="output/SwiftBindingsTestLib.cs"
if [ ! -f "$BINDINGS_FILE" ]; then
    echo "ERROR: Bindings not found at $BINDINGS_FILE"
    echo "Run build-and-test.sh first to generate bindings."
    exit 1
fi
NEWEST_SWIFT=$(find Sources/SwiftBindingsTestLib -name '*.swift' -newer "$BINDINGS_FILE" 2>/dev/null | head -1)
if [ -n "$NEWEST_SWIFT" ]; then
    echo "ERROR: Bindings are stale. Swift source newer than bindings:"
    echo "  $NEWEST_SWIFT"
    echo "Run build-and-test.sh to regenerate."
    exit 1
fi
echo "Bindings are up to date."
echo ""

# --- Step 1: Safety attribute check ---
echo "--- Step 1: Safety attributes use DiagnosticId (no downgrade needed) ---"
echo "Safety attributes use DiagnosticId (SB0001/SB0002) — no sed downgrade needed."
echo ""

# --- Step 2: Publish NativeAOT projects ---
mkdir -p logs

MAIN_BUNDLE_ID="com.swiftbindings.nativeaottestapp"
NONBLITTABLE_BUNDLE_ID="com.swiftbindings.nativeaottestapp.nonblittable"
MAIN_APP_PATH=""
NONBLITTABLE_APP_PATH=""
NONBLITTABLE_AVAILABLE=false

if [ "$SKIP_PUBLISH" = false ]; then
    echo "--- Step 2a: Publish main NativeAotTestApp ---"
    cd NativeAotTestApp
    rm -rf bin obj
    echo "Publishing with NativeAOT (this may take several minutes)..."
    if dotnet publish -c Release 2>&1 | tee ../logs/nativeaot-publish-main.log | tail -20; then
        echo ""
        echo "Main project published successfully."
    else
        echo ""
        echo "ERROR: Main project publish failed. See logs/nativeaot-publish-main.log"
        exit 1
    fi
    cd ..
    echo ""

    echo "--- Step 2b: Publish NonBlittable project ---"
    cd NativeAotTestApp.NonBlittable
    rm -rf bin obj
    echo "Publishing NonBlittable project..."
    if dotnet publish -c Release 2>&1 | tee ../logs/nativeaot-publish-nonblittable.log | tail -20; then
        echo ""
        echo "NonBlittable project published successfully."
        NONBLITTABLE_AVAILABLE=true
    else
        echo ""
        echo "NonBlittable project FAILED to publish (expected for Blocker 2)."
        echo "ILCompiler rejected non-blittable CallConvSwift signatures at compile time."
        record_result "COMPILE_FAIL" "b2-optional-dllimport" "ILCompiler rejected at compile time"
        record_result "COMPILE_FAIL" "b2-safehandle-dllimport" "ILCompiler rejected at compile time"
        record_result "COMPILE_FAIL" "b2-optional-libimport" "ILCompiler rejected at compile time"
        record_result "COMPILE_FAIL" "b2-optional-marshaller" "ILCompiler rejected at compile time"
    fi
    cd ..
    echo ""
else
    echo "--- Step 2: Skipped (--skip-publish) ---"
    echo ""
fi

# --- Step 3: Locate app bundles ---
echo "--- Step 3: Locate app bundles ---"
MAIN_APP_PATH=$(find NativeAotTestApp/bin -name "NativeAotTestApp.app" -type d 2>/dev/null | head -1)
if [ -z "$MAIN_APP_PATH" ]; then
    echo "ERROR: Main app bundle not found. Run without --skip-publish."
    exit 1
fi
echo "Main app: $MAIN_APP_PATH"

if [ "$NONBLITTABLE_AVAILABLE" = true ] || [ "$SKIP_PUBLISH" = true ]; then
    NONBLITTABLE_APP_PATH=$(find NativeAotTestApp.NonBlittable/bin -name "NativeAotTestApp.NonBlittable.app" -type d 2>/dev/null | head -1)
    if [ -n "$NONBLITTABLE_APP_PATH" ]; then
        echo "NonBlittable app: $NONBLITTABLE_APP_PATH"
        NONBLITTABLE_AVAILABLE=true
    else
        echo "NonBlittable app bundle not found."
        NONBLITTABLE_AVAILABLE=false
    fi
fi
echo ""

# --- Step 4: Ensure simulator is booted ---
echo "--- Step 4: Ensure simulator is booted ---"
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
fi
echo ""

# --- Step 5: Define test lists ---
MAIN_MUST_PASS_TESTS="b1-string-create b1-string-length b1-string-wrapper b1-existential b1-generated-binding"
MAIN_CRASHRISK_TESTS="cr-enum-basic cr-enum-string cr-enum-shape cr-enum-nested cr-array-basic cr-array-advanced cr-gc-basic cr-gc-mutableprops cr-gc-stress cr-existential"
MAIN_INVESTIGATIVE_TESTS="b1-vwt-destroy b1-vwt-initcopy b2-intptr-manual b3-async-safehandle b3-async-static b3-async-wrapper n1-moduleinit n3-trimming cd-dispose-class cd-dispose-struct-string cd-dispose-struct-nested"
MAIN_NO_INJECT_TESTS="n2-resolve-no-inject"
MAIN_WITH_INJECT_TESTS="n2-resolve-with-inject"
NONBLITTABLE_TESTS="b2-optional-dllimport b2-safehandle-dllimport b2-optional-libimport b2-optional-marshaller"

CRASH_LOG_DIR="$HOME/Library/Logs/DiagnosticReports"

# --- Helper: Run a single test ---
run_test() {
    local app_path="$1"
    local bundle_id="$2"
    local test_id="$3"

    # Apply filter
    if [ -n "$TEST_FILTER" ] && [ "$TEST_FILTER" != "$test_id" ]; then
        return
    fi

    echo "  Running: $test_id"

    # Snapshot crash logs before launch
    CRASH_BEFORE=$(ls -1 "$CRASH_LOG_DIR"/NativeAotTestApp*.ips 2>/dev/null | wc -l || echo 0)

    # Terminate any lingering process
    xcrun simctl terminate booted "$bundle_id" 2>/dev/null || true

    # Launch and capture output
    local output_file
    output_file=$(mktemp)
    xcrun simctl launch --console booted "$bundle_id" --test-id "$test_id" > "$output_file" 2>&1 &
    local launch_pid=$!

    # Poll with timeout (macOS lacks timeout command)
    local elapsed=0
    while kill -0 $launch_pid 2>/dev/null && [ $elapsed -lt $PER_TEST_TIMEOUT ]; do
        sleep 1
        elapsed=$((elapsed + 1))

        # Check for early completion
        if grep -q "^PASS:\|^FAIL:" "$output_file" 2>/dev/null; then
            # Give a moment for process to finish writing
            sleep 1
            break
        fi
    done

    # Kill if still running
    kill $launch_pid 2>/dev/null || true
    wait $launch_pid 2>/dev/null || true

    # Terminate the app
    xcrun simctl terminate booted "$bundle_id" 2>/dev/null || true

    # Parse output
    local test_output
    test_output=$(cat "$output_file" 2>/dev/null)
    rm -f "$output_file"

    # Check for PASS/FAIL markers
    if echo "$test_output" | grep -q "^PASS: $test_id"; then
        local detail
        detail=$(echo "$test_output" | grep "^PASS: $test_id" | head -1 | sed "s/^PASS: $test_id//;s/^ *//")
        record_result "PASS" "$test_id" "$detail"
    elif echo "$test_output" | grep -q "^FAIL: $test_id"; then
        local detail
        detail=$(echo "$test_output" | grep "^FAIL: $test_id" | head -1 | sed "s/^FAIL: $test_id: *//")
        record_result "FAIL" "$test_id" "$detail"
    else
        # Check for crash
        CRASH_AFTER=$(ls -1 "$CRASH_LOG_DIR"/NativeAotTestApp*.ips 2>/dev/null | wc -l || echo 0)
        if [ "$CRASH_AFTER" -gt "$CRASH_BEFORE" ]; then
            record_result "CRASH" "$test_id" "New crash log detected"
        elif echo "$test_output" | grep -q "SIGABRT\|SIGSEGV\|SIGBUS\|Fatal error\|EXC_BAD_ACCESS\|Assertion.*not met"; then
            record_result "CRASH" "$test_id" "Crash signal in output"
        elif [ $elapsed -ge $PER_TEST_TIMEOUT ]; then
            record_result "FAIL" "$test_id" "Timed out after ${PER_TEST_TIMEOUT}s"
        else
            # Process exited without markers — likely a crash
            record_result "CRASH" "$test_id" "No PASS/FAIL marker, process exited"
        fi
    fi
}

# --- Step 6: Install and run tests ---
echo "--- Step 6: Run tests ---"
echo ""

# Install main app
echo "Installing main app..."
xcrun simctl install booted "$MAIN_APP_PATH"
echo ""

# Phase 1: No-injection mode (test framework resolution without manual dylib copy)
if [ "$NO_INJECT" = true ] || [ -z "$TEST_FILTER" ] || [ "$TEST_FILTER" = "n2-resolve-no-inject" ]; then
    echo "=== Phase 1: No-injection tests ==="
    for test_id in $MAIN_NO_INJECT_TESTS; do
        run_test "$MAIN_APP_PATH" "$MAIN_BUNDLE_ID" "$test_id"
    done
    echo ""
fi

# Phase 2: With-injection mode (copy dylib into Frameworks/)
if [ "$NO_INJECT" = false ]; then
    echo "=== Phase 2: Injecting libSwiftBindingsRuntime.dylib ==="
    RUNTIME_DYLIB="../src/Swift.Runtime/native/iossimulator/libSwiftBindingsRuntime.dylib"
    APP_FRAMEWORKS="$MAIN_APP_PATH/Frameworks"
    if [ -f "$RUNTIME_DYLIB" ]; then
        mkdir -p "$APP_FRAMEWORKS"
        cp "$RUNTIME_DYLIB" "$APP_FRAMEWORKS/"
        echo "Injected libSwiftBindingsRuntime.dylib into app bundle."
        # Re-install after injection
        xcrun simctl install booted "$MAIN_APP_PATH"
    else
        echo "Warning: libSwiftBindingsRuntime.dylib not found at $RUNTIME_DYLIB"
        echo "Existential metadata tests may fail."
    fi
    echo ""

    echo "=== Phase 3: Must-pass tests (Blocker 1) ==="
    for test_id in $MAIN_MUST_PASS_TESTS; do
        run_test "$MAIN_APP_PATH" "$MAIN_BUNDLE_ID" "$test_id"
    done
    echo ""

    echo "=== Phase 4: CrashRisk tests (Mono JIT crashers — must pass under NativeAOT) ==="
    for test_id in $MAIN_CRASHRISK_TESTS; do
        run_test "$MAIN_APP_PATH" "$MAIN_BUNDLE_ID" "$test_id"
    done
    echo ""

    echo "=== Phase 5: Investigative tests ==="
    for test_id in $MAIN_INVESTIGATIVE_TESTS; do
        run_test "$MAIN_APP_PATH" "$MAIN_BUNDLE_ID" "$test_id"
    done
    echo ""

    echo "=== Phase 6: With-injection resolution tests ==="
    for test_id in $MAIN_WITH_INJECT_TESTS; do
        run_test "$MAIN_APP_PATH" "$MAIN_BUNDLE_ID" "$test_id"
    done
    echo ""
fi

# Phase 7: NonBlittable tests (if project compiled)
if [ "$NONBLITTABLE_AVAILABLE" = true ]; then
    echo "=== Phase 7: NonBlittable tests (Blocker 2) ==="
    echo "Installing NonBlittable app..."
    xcrun simctl install booted "$NONBLITTABLE_APP_PATH"
    for test_id in $NONBLITTABLE_TESTS; do
        run_test "$NONBLITTABLE_APP_PATH" "$NONBLITTABLE_BUNDLE_ID" "$test_id"
    done
    echo ""
fi

# --- Step 7: Results summary ---
echo ""
echo "========================================="
echo " NativeAOT Test Results"
echo "========================================="
echo ""

# Count must-pass results
MUST_PASS_PASSED=0
MUST_PASS_TOTAL=0
for test_id in $MAIN_MUST_PASS_TESTS; do
    MUST_PASS_TOTAL=$((MUST_PASS_TOTAL + 1))
    for r in "${RESULTS[@]}"; do
        if echo "$r" | grep -q "^PASS: $test_id"; then
            MUST_PASS_PASSED=$((MUST_PASS_PASSED + 1))
            break
        fi
    done
done

# Count CrashRisk results
CRASHRISK_PASSED=0
CRASHRISK_TOTAL=0
for test_id in $MAIN_CRASHRISK_TESTS; do
    CRASHRISK_TOTAL=$((CRASHRISK_TOTAL + 1))
    for r in "${RESULTS[@]}"; do
        if echo "$r" | grep -q "^PASS: $test_id"; then
            CRASHRISK_PASSED=$((CRASHRISK_PASSED + 1))
            break
        fi
    done
done

for r in "${RESULTS[@]}"; do
    echo "  $r"
done

echo ""
echo "-----------------------------------------"
echo "  Must-pass:     $MUST_PASS_PASSED/$MUST_PASS_TOTAL passed"
echo "  CrashRisk:     $CRASHRISK_PASSED/$CRASHRISK_TOTAL passed"
echo "  Total passed:  $PASS_COUNT"
echo "  Total failed:  $FAIL_COUNT"
echo "  Total crashed: $CRASH_COUNT"
echo "  Compile-fail:  $COMPILE_FAIL_COUNT"
echo "-----------------------------------------"
echo ""

# Capture app bundle size for comparison
if [ -n "$MAIN_APP_PATH" ]; then
    APP_SIZE=$(du -sh "$MAIN_APP_PATH" 2>/dev/null | cut -f1)
    echo "Main app bundle size: $APP_SIZE"
fi

# Save logs summary
echo ""
echo "Publish logs:"
echo "  logs/nativeaot-publish-main.log"
echo "  logs/nativeaot-publish-nonblittable.log"

# Count trimming/AOT warnings in publish log
if [ -f "logs/nativeaot-publish-main.log" ]; then
    IL_WARNINGS=$(grep -c "warning IL" logs/nativeaot-publish-main.log 2>/dev/null || echo 0)
    echo "  Trimming/AOT warnings in main: $IL_WARNINGS"
fi

echo ""

# Exit with failure if must-pass tests didn't all pass
if [ "$MUST_PASS_PASSED" -lt "$MUST_PASS_TOTAL" ]; then
    echo "OVERALL: FAIL ($MUST_PASS_PASSED/$MUST_PASS_TOTAL must-pass tests passed)"
    exit 1
else
    echo "OVERALL: PASS (all must-pass tests passed)"
    exit 0
fi
