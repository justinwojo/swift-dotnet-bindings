#!/bin/bash
# Validates BlinkIdTestApp on iOS Simulator
# Usage: ./validate-sim.sh [timeout_seconds]
#
# Returns exit code 0 on success (all tests pass, or only known failures), 1 on crash/unexpected failure
#
# Known failures: all caused by non-blittable SwiftString in P/Invoke with Swift calling convention
# (.NET Mono JIT limitation). These are expected and tracked in remaining-work.md.
# The exact test names are checked so that a fixed known failure + new unexpected failure is caught.

set -euo pipefail

TIMEOUT=${1:-10}
# Known failure test names (matched against "  - <name>:" lines in app output).
# Update this list when known failures are fixed or new ones are identified.
KNOWN_FAILURE_NAMES=(
    "DetectionStatus cases"
    "Country raw value"
    "DocumentType raw value"
)
APP_PATH="BlinkIdTestApp/bin/Debug/net10.0-ios/iossimulator-arm64/BlinkIdTestApp.app"
BUNDLE_ID="com.swiftbindings.blinkidtestapp"
CRASH_LOG_DIR="$HOME/Library/Logs/DiagnosticReports"

cd "$(dirname "$0")"

# Count crash logs using glob array (avoids pipe + pipefail interaction)
count_crash_logs() {
    local files=( "$CRASH_LOG_DIR"/BlinkIdTestApp*.ips )
    if [[ -e "${files[0]}" ]]; then
        echo "${#files[@]}"
    else
        echo 0
    fi
}

# Record crash log count before running
BEFORE_CRASH_COUNT=$(count_crash_logs)

# Install the app
echo "Installing app..."
xcrun simctl install booted "$APP_PATH"

# Launch and capture output with timeout
echo "Launching app (timeout: ${TIMEOUT}s)..."
OUTPUT_FILE=$(mktemp)
xcrun simctl launch --console --terminate-running-process booted "$BUNDLE_ID" > "$OUTPUT_FILE" 2>&1 &
PID=$!

# Wait for timeout or specific markers
ELAPSED=0
RESULT="timeout"
while [ $ELAPSED -lt $TIMEOUT ]; do
    sleep 1
    ELAPSED=$((ELAPSED + 1))

    # Check for final success marker (all tests pass)
    if grep -q "TEST SUCCESS" "$OUTPUT_FILE" 2>/dev/null; then
        RESULT="success"
        break
    fi

    # Check for test failure marker (some tests failed, app completed)
    if grep -q "TEST FAILURE" "$OUTPUT_FILE" 2>/dev/null; then
        RESULT="test_failure"
        break
    fi

    # Check for crash/error markers
    if grep -q "SIGABRT\|SIGSEGV\|SIGBUS\|Fatal error\|CRASH\|EXC_BAD_ACCESS" "$OUTPUT_FILE" 2>/dev/null; then
        echo "=== CRASH DETECTED ==="
        cat "$OUTPUT_FILE"
        rm -f "$OUTPUT_FILE"
        kill $PID 2>/dev/null || true
        exit 1
    fi
done

# Terminate the app
xcrun simctl terminate booted "$BUNDLE_ID" 2>/dev/null || true
kill $PID 2>/dev/null || true

# Check for new crash logs
AFTER_CRASH_COUNT=$(count_crash_logs)
if [ "$AFTER_CRASH_COUNT" -gt "$BEFORE_CRASH_COUNT" ]; then
    echo "=== CRASH LOG DETECTED ==="
    ls -t "$CRASH_LOG_DIR"/BlinkIdTestApp*.ips | head -1 | xargs head -50
    rm -f "$OUTPUT_FILE"
    exit 1
fi

# Show output
echo "=== APP OUTPUT ==="
cat "$OUTPUT_FILE"

if [ "$RESULT" = "success" ]; then
    echo ""
    echo "=== VALIDATION PASSED ==="
    rm -f "$OUTPUT_FILE"
    exit 0
elif [ "$RESULT" = "test_failure" ]; then
    # Extract failed test names from lines containing "  - <name>: <reason>".
    # Actual format is "[0.650s] [FAIL]   - DetectionStatus cases: reason" because
    # TestLogger prepends a timestamp and category prefix.
    UNEXPECTED=()
    MATCHED_KNOWN=0
    while IFS= read -r line; do
        # Extract everything after "  - " up to the next ":"
        TEST_NAME="${line#*  - }"
        TEST_NAME="${TEST_NAME%%:*}"
        IS_KNOWN=false
        for KNOWN in "${KNOWN_FAILURE_NAMES[@]}"; do
            if [ "$TEST_NAME" = "$KNOWN" ]; then
                IS_KNOWN=true
                MATCHED_KNOWN=$((MATCHED_KNOWN + 1))
                break
            fi
        done
        if [ "$IS_KNOWN" = false ]; then
            UNEXPECTED+=("$TEST_NAME")
        fi
    done < <(grep "  - " "$OUTPUT_FILE" | grep "\[FAIL\]" || true)
    rm -f "$OUTPUT_FILE"

    if [ ${#UNEXPECTED[@]} -eq 0 ] && [ "$MATCHED_KNOWN" -gt 0 ]; then
        echo ""
        echo "=== VALIDATION PASSED (with $MATCHED_KNOWN known failures) ==="
        exit 0
    else
        echo ""
        if [ ${#UNEXPECTED[@]} -gt 0 ]; then
            echo "=== VALIDATION FAILED: ${#UNEXPECTED[@]} unexpected failure(s) ==="
            for NAME in "${UNEXPECTED[@]}"; do
                echo "  UNEXPECTED: $NAME"
            done
        fi
        if [ "$MATCHED_KNOWN" -eq 0 ]; then
            echo "=== VALIDATION FAILED: could not identify any known failures in output ==="
        fi
        exit 1
    fi
else
    rm -f "$OUTPUT_FILE"
    echo ""
    echo "=== TIMEOUT (no success marker found) ==="
    exit 1
fi
