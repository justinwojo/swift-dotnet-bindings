#!/bin/bash
# Generator coverage test for BlinkIDUX
#
# Generates a TBD file from the BlinkIDUX dylib and runs the binding
# generator. Validates that:
#   - The generator doesn't hard-crash
#   - A binding report is produced
#   - At least one non-SwiftUI type is emitted
#   - At least one skipped item references SwiftUI/SwiftUICore
#
# Non-zero generator exit code is treated as a warning (expected when
# SwiftUI types cause skips), not a failure.

set -euo pipefail

cd "$(dirname "$0")"
PROJECT_ROOT="../.."

# --- Pre-flight checks ---

if ! command -v xcrun &>/dev/null; then
    echo "Error: xcrun not found. Install Xcode Command Line Tools."
    exit 1
fi

if ! xcrun --find tapi &>/dev/null; then
    echo "Error: tapi not found. Ensure Xcode is installed."
    exit 1
fi

ABI_JSON="BlinkIDUX.xcframework/ios-arm64_x86_64-simulator/BlinkIDUX.framework/Modules/BlinkIDUX.swiftmodule/arm64-apple-ios-simulator.abi.json"
DYLIB_PATH="BlinkIDUX.xcframework/ios-arm64_x86_64-simulator/BlinkIDUX.framework/BlinkIDUX"

if [ ! -f "$ABI_JSON" ]; then
    echo "Error: ABI JSON not found at $ABI_JSON"
    echo "Ensure BlinkIDUX.xcframework is present."
    exit 1
fi

if [ ! -f "$DYLIB_PATH" ]; then
    echo "Error: BlinkIDUX dylib not found at $DYLIB_PATH"
    exit 1
fi

# --- Setup ---

mkdir -p output-ux
TBD_FILE="output-ux/BlinkIDUX.tbd"

# Generate TBD if missing or empty
if [ ! -s "$TBD_FILE" ]; then
    echo "Generating TBD file..."
    xcrun tapi stubify --filetype=tbd-v4 "$DYLIB_PATH" -o "$TBD_FILE"
fi

# --- Run generator ---

echo "Running binding generator on BlinkIDUX..."
set +e
dotnet run --project "$PROJECT_ROOT/src/Swift.Bindings/src" -- \
  -a "$ABI_JSON" \
  -d "$DYLIB_PATH" \
  -t "$TBD_FILE" \
  -o "output-ux" -l BlinkIDUX --async-library SwiftBindings
GEN_EXIT=$?
set -e

echo "$GEN_EXIT" > output-ux/generator-exit-code

# Check for hard crash (signal-based exit codes)
if [ $GEN_EXIT -ge 128 ]; then
    SIGNAL=$((GEN_EXIT - 128))
    echo "FAIL: Generator crashed with signal $SIGNAL (exit code $GEN_EXIT)"
    exit 1
fi

if [ $GEN_EXIT -ne 0 ]; then
    echo "Warning: Generator exited with code $GEN_EXIT (expected for SwiftUI-heavy modules)"
fi

# --- Validation ---

REPORT="output-ux/binding-report.json"
PASS=true

# Check 1: Binding report exists
if [ ! -f "$REPORT" ]; then
    echo "FAIL: binding-report.json not found"
    exit 1
fi
echo "OK: binding-report.json exists"

# Check 2: At least one non-SwiftUI type emitted
EMITTED=$(grep -o '"EmittedTypes": *[0-9]*' "$REPORT" | grep -o '[0-9]*')
if [ -z "$EMITTED" ] || [ "$EMITTED" -eq 0 ]; then
    echo "FAIL: No types were emitted (expected at least one non-SwiftUI type)"
    PASS=false
else
    echo "OK: $EMITTED type(s) emitted"
fi

# Check 3: At least one skipped item references SwiftUI/SwiftUICore
# Use jq to scope to SkippedItems when available; fall back to grep within
# the SkippedItems JSON array (between "SkippedItems" and closing bracket).
if command -v jq &>/dev/null; then
    SWIFTUI_SKIPS=$(jq '[.SkippedItems[] | select(.Details // "" | test("SwiftUI|SwiftUICore"))] | length' "$REPORT" 2>/dev/null || echo 0)
else
    # Extract SkippedItems block and grep within it
    SWIFTUI_SKIPS=$(sed -n '/"SkippedItems"/,/^  \]/p' "$REPORT" | grep -c "SwiftUI\|SwiftUICore" || true)
fi
if [ "$SWIFTUI_SKIPS" -gt 0 ] 2>/dev/null; then
    echo "OK: $SWIFTUI_SKIPS skipped item(s) reference SwiftUI/SwiftUICore"
else
    echo "FAIL: No skipped items reference SwiftUI (SwiftUI detection not working)"
    PASS=false
fi

# --- Summary ---

echo ""
echo "=== BlinkIDUX Generator Coverage Summary ==="

# Print key metrics from report (no jq required)
TOTAL_TYPES=$(grep -o '"TotalTypes": *[0-9]*' "$REPORT" | grep -o '[0-9]*' || echo "?")
SKIPPED_TYPES=$(grep -o '"SkippedTypes": *[0-9]*' "$REPORT" | grep -o '[0-9]*' || echo "?")
TOTAL_MEMBERS=$(grep -o '"TotalMembers": *[0-9]*' "$REPORT" | grep -o '[0-9]*' || echo "?")
EMITTED_MEMBERS=$(grep -o '"EmittedMembers": *[0-9]*' "$REPORT" | grep -o '[0-9]*' || echo "?")
SKIPPED_MEMBERS=$(grep -o '"SkippedMembers": *[0-9]*' "$REPORT" | grep -o '[0-9]*' || echo "?")

echo "  Types:   $EMITTED/$TOTAL_TYPES emitted, $SKIPPED_TYPES skipped"
echo "  Members: $EMITTED_MEMBERS/$TOTAL_MEMBERS emitted, $SKIPPED_MEMBERS skipped"
echo "  Generator exit code: $GEN_EXIT"

# Pretty-print skip reasons if jq is available
if command -v jq &>/dev/null; then
    echo ""
    echo "  Skip reasons:"
    jq -r '.SkippedItems[] | "    \(.Reason): \(.ContainingType).\(.Name)"' "$REPORT" 2>/dev/null || true
fi

echo ""
if [ "$PASS" = true ]; then
    echo "=== GENERATOR COVERAGE PASSED ==="
else
    echo "=== GENERATOR COVERAGE FAILED ==="
    exit 1
fi
