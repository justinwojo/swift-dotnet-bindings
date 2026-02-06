#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Generator coverage test for BridgeParamTestLib
#
# Runs the binding generator on the BridgeParamTestLib xcframework.
# Validates that:
#   - The generator doesn't hard-crash
#   - A binding report is produced
#   - SwiftUI views are detected (bridge files generated)
#
# Non-zero generator exit code is treated as a warning (expected when
# SwiftUI types cause skips), not a failure.

set -euo pipefail
cd "$(dirname "$0")"

# Platform check
if [ "$(uname -s)" != "Darwin" ]; then
    echo "Error: This script requires macOS (Darwin)."
    exit 1
fi

PROJECT_ROOT="../.."

# --- Pre-flight checks ---

if ! command -v xcrun &>/dev/null; then
    echo "Error: xcrun not found. Install Xcode Command Line Tools."
    exit 1
fi

MODULE_NAME="BridgeParamTestLib"
XCFW_DIR=".build/$MODULE_NAME.xcframework"
SIM_FW_DIR=$(find "$XCFW_DIR" -type d -name "*.framework" | head -1)

if [ ! -d "$XCFW_DIR" ]; then
    echo "Error: xcframework not found at $XCFW_DIR"
    echo "Run ./build-xcframework.sh first."
    exit 1
fi

# Find the ABI JSON, dylib, and TBD
ABI_JSON=$(find "$SIM_FW_DIR" -name "*.abi.json" | head -1)
DYLIB="$SIM_FW_DIR/$MODULE_NAME"
TBD=$(find "$SIM_FW_DIR" -name "*.tbd" | head -1)

if [ -z "$ABI_JSON" ]; then
    echo "Error: ABI JSON not found in $SIM_FW_DIR"
    exit 1
fi

if [ ! -f "$DYLIB" ]; then
    echo "Error: dylib not found at $DYLIB"
    exit 1
fi

echo "=== Running binding generator on $MODULE_NAME ==="
echo "ABI JSON: $ABI_JSON"
echo "Dylib: $DYLIB"
echo "TBD: $TBD"

# --- Setup ---
mkdir -p output

# --- Run generator ---
set +e
dotnet run --project "$PROJECT_ROOT/src/Swift.Bindings/src" -- \
  -a "$ABI_JSON" \
  -d "$DYLIB" \
  -t "$TBD" \
  -o "output" \
  -l "$MODULE_NAME" 2>&1
GEN_EXIT=$?
set -e

echo "$GEN_EXIT" > output/generator-exit-code

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
REPORT="output/binding-report.json"
PASS=true

# Check 1: Binding report exists
if [ ! -f "$REPORT" ]; then
    echo "FAIL: binding-report.json not found"
    exit 1
fi
echo "OK: binding-report.json exists"

# Check 2: Bridge Swift file generated (SwiftUI views detected)
BRIDGE_SWIFT="output/Swift.$MODULE_NAME.SwiftUIBridge.swift"
if [ -f "$BRIDGE_SWIFT" ]; then
    echo "OK: Bridge Swift file generated: $BRIDGE_SWIFT"
    LINE_COUNT=$(wc -l < "$BRIDGE_SWIFT" | tr -d ' ')
    echo "    ($LINE_COUNT lines)"
else
    echo "FAIL: Bridge Swift file not generated"
    PASS=false
fi

# Check 3: Bridge C# file generated
BRIDGE_CS="output/Swift.$MODULE_NAME.SwiftUIBridge.cs"
if [ -f "$BRIDGE_CS" ]; then
    echo "OK: Bridge C# file generated: $BRIDGE_CS"
else
    echo "FAIL: Bridge C# file not generated"
    PASS=false
fi

# Check 4: swiftc typecheck on generated bridge (fast syntax validation)
# For InferredAsync views (data-driven emission), typecheck errors are hard failures.
# For legacy dictionary-based patterns, typecheck errors remain warnings.
if [ -f "$BRIDGE_SWIFT" ]; then
    echo "=== Swift typecheck on generated bridge ==="
    SIM_SDK=$(xcrun --sdk iphonesimulator --show-sdk-path)
    set +e
    xcrun swiftc -typecheck -target arm64-apple-ios16.0-simulator \
      -sdk "$SIM_SDK" \
      -F ".build/$MODULE_NAME.xcframework/ios-arm64-simulator/" \
      "$BRIDGE_SWIFT" 2>&1
    TC_EXIT=$?
    set -e

    # Check if any InferredAsync views were Generated (data-driven emission)
    HAS_INFERRED_ASYNC=false
    if [ -f "$REPORT" ]; then
        if command -v jq &>/dev/null; then
            INFERRED_COUNT=$(jq '[.BridgedViews[]? | select(.InitClassification == "InferredAsync" and .BridgeStatus == "Generated")] | length' "$REPORT" 2>/dev/null || echo "0")
        else
            # Fallback: awk scans each JSON object block for both fields (order-independent)
            INFERRED_COUNT=$(awk '/{/{a=0;b=0} /"InitClassification".*"InferredAsync"/{a=1} /"BridgeStatus".*"Generated"/{b=1} /}/{if(a&&b)n++;a=0;b=0} END{print n+0}' "$REPORT" 2>/dev/null || echo "0")
        fi
        if [ "$INFERRED_COUNT" -gt 0 ]; then
            HAS_INFERRED_ASYNC=true
            echo "  ($INFERRED_COUNT InferredAsync view(s) with Generated status)"
        fi
    fi

    if [ $TC_EXIT -eq 0 ]; then
        echo "OK: Generated bridge passes typecheck"
    elif [ "$HAS_INFERRED_ASYNC" = true ]; then
        echo "FAIL: Generated bridge has typecheck errors for InferredAsync views (exit code $TC_EXIT)"
        PASS=false
    else
        echo "Warning: Generated bridge has typecheck errors (exit code $TC_EXIT)"
        echo "  (May need test helpers for full compilation)"
    fi
fi

# --- Summary ---
echo ""
echo "=== $MODULE_NAME Generator Summary ==="

# Print key metrics from report
TOTAL_TYPES=$(grep -o '"TotalTypes": *[0-9]*' "$REPORT" | grep -o '[0-9]*' || echo "?")
EMITTED=$(grep -o '"EmittedTypes": *[0-9]*' "$REPORT" | grep -o '[0-9]*' || echo "?")
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
