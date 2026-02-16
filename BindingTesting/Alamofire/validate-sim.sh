#!/bin/bash
# Validates AlamofireTestApp on iOS Simulator
# Usage: ./validate-sim.sh [timeout_seconds]
#
# Returns exit code 0 on success, 1 on failure/crash

set -e

TIMEOUT=${1:-15}
APP_PATH="AlamofireTestApp/bin/Debug/net10.0-ios/iossimulator-arm64/AlamofireTestApp.app"
BUNDLE_ID="com.swiftbindings.alamofiretestapp"
CRASH_LOG_DIR="$HOME/Library/Logs/DiagnosticReports"

cd "$(dirname "$0")"

# Record crash log timestamps before running
BEFORE_CRASH_COUNT=$(ls -1 "$CRASH_LOG_DIR"/AlamofireTestApp*.ips 2>/dev/null | wc -l || echo 0)

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
SUCCESS=false
while [ $ELAPSED -lt $TIMEOUT ]; do
    sleep 1
    ELAPSED=$((ELAPSED + 1))

    # Check for final success marker
    if grep -q "TEST SUCCESS" "$OUTPUT_FILE" 2>/dev/null; then
        SUCCESS=true
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
AFTER_CRASH_COUNT=$(ls -1 "$CRASH_LOG_DIR"/AlamofireTestApp*.ips 2>/dev/null | wc -l || echo 0)
if [ "$AFTER_CRASH_COUNT" -gt "$BEFORE_CRASH_COUNT" ]; then
    echo "=== CRASH LOG DETECTED ==="
    ls -t "$CRASH_LOG_DIR"/AlamofireTestApp*.ips | head -1 | xargs head -50
    rm -f "$OUTPUT_FILE"
    exit 1
fi

# Show output
echo "=== APP OUTPUT ==="
cat "$OUTPUT_FILE"
rm -f "$OUTPUT_FILE"

if [ "$SUCCESS" = true ]; then
    echo ""
    echo "=== VALIDATION PASSED ==="
    exit 0
else
    echo ""
    echo "=== TIMEOUT (no success marker found) ==="
    exit 1
fi
