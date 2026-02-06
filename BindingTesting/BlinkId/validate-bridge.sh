#!/bin/bash
# Validates BlinkIDUXTestApp on iOS Simulator
# Usage: ./validate-bridge.sh [timeout_seconds]
#
# Returns exit code 0 on success, 1 on crash/failure/timeout

set -euo pipefail

TIMEOUT=${1:-15}
APP_PATH="BlinkIDUXTestApp/bin/Debug/net10.0-ios/iossimulator-arm64/BlinkIDUXTestApp.app"
BUNDLE_ID="com.swiftbindings.blinkiduxtest"
CRASH_LOG_DIR="$HOME/Library/Logs/DiagnosticReports"

cd "$(dirname "$0")"

# Ensure a simulator is booted
if ! xcrun simctl list devices booted 2>/dev/null | grep -q "Booted"; then
    echo "Error: No iOS Simulator is booted."
    echo "Boot one with: xcrun simctl boot <device-id>"
    echo "List available devices with: xcrun simctl list devices available"
    exit 1
fi

# Count crash logs using glob array (avoids pipe + pipefail interaction)
count_crash_logs() {
    local files=( "$CRASH_LOG_DIR"/BlinkIDUXTestApp*.ips )
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

    # Check for final success marker
    if grep -q "TEST SUCCESS" "$OUTPUT_FILE" 2>/dev/null; then
        RESULT="success"
        break
    fi

    # Check for test failure marker
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
    ls -t "$CRASH_LOG_DIR"/BlinkIDUXTestApp*.ips | head -1 | xargs head -50
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
    echo ""
    echo "=== VALIDATION FAILED ==="
    rm -f "$OUTPUT_FILE"
    exit 1
else
    rm -f "$OUTPUT_FILE"
    echo ""
    echo "=== TIMEOUT (no success marker found) ==="
    exit 1
fi
