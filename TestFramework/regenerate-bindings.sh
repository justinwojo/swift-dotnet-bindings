#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Regenerates C# bindings from the built xcframework.
# Requires: build-xcframework.sh to have been run first.
#
# Usage: ./regenerate-bindings.sh [--strict]
#
# Options:
#   --strict    Fail if the generator exits with a non-zero exit code.
#               Without this flag, non-zero exits are reported but tolerated.

set -e
cd "$(dirname "$0")"

STRICT=false
while [[ $# -gt 0 ]]; do
    case $1 in
        --strict)
            STRICT=true
            shift
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

MODULE_NAME="SwiftBindingsTestLib"
PROJECT_ROOT=".."
XCFW_DIR=".build/$MODULE_NAME.xcframework"
SIM_FW_DIR=$(find "$XCFW_DIR" -type d -name "*.framework" | head -1)

if [ ! -d "$XCFW_DIR" ]; then
    echo "Error: xcframework not found at $XCFW_DIR"
    echo "Run ./build-xcframework.sh first."
    exit 1
fi

echo "=== Regenerating bindings for $MODULE_NAME ==="
echo "Framework: $SIM_FW_DIR"

# Find the ABI JSON, dylib, TBD, and swiftinterface
ABI_JSON=$(find "$SIM_FW_DIR" -name "*.abi.json" | head -1)
DYLIB="$SIM_FW_DIR/$MODULE_NAME"
TBD=$(find "$SIM_FW_DIR" -name "*.tbd" | head -1)
SWIFTINTERFACE=$(find "$XCFW_DIR" -name "*.swiftinterface" | head -1)

if [ -z "$ABI_JSON" ]; then
    echo "Error: ABI JSON not found in $SIM_FW_DIR"
    exit 1
fi

echo "ABI JSON: $ABI_JSON"
echo "Dylib: $DYLIB"
echo "TBD: $TBD"
echo "SwiftInterface: $SWIFTINTERFACE"

# Create output directory
mkdir -p output

# Run the binding generator.
# Note: The generator may crash on certain advanced Swift features (existentials,
# unbound generics, protocol compositions) that are intentionally included in the
# test library to exercise these code paths. A non-zero exit code from the generator
# is reported but does not fail this script — check output/ for partial results.
set +e
SWIFTINTERFACE_OPT=""
if [ -n "$SWIFTINTERFACE" ]; then
    SWIFTINTERFACE_OPT="-s $SWIFTINTERFACE"
fi

SYMBOLGRAPH_OPT=""
SYMBOLGRAPH_DIR=".build/symbolgraph"
if [ -d "$SYMBOLGRAPH_DIR" ]; then
    SG_COUNT=$(find "$SYMBOLGRAPH_DIR" -name "*.symbols.json" 2>/dev/null | wc -l | tr -d ' ')
    if [ "$SG_COUNT" -gt 0 ]; then
        SYMBOLGRAPH_OPT="--symbolgraph $SYMBOLGRAPH_DIR"
        echo "Symbol graph: $SYMBOLGRAPH_DIR ($SG_COUNT files)"
    fi
fi

dotnet run --project "$PROJECT_ROOT/src/Swift.Bindings/src" -- \
    -a "$ABI_JSON" \
    -d "$DYLIB" \
    -t "$TBD" \
    -o output \
    -l "$MODULE_NAME" \
    $SWIFTINTERFACE_OPT \
    $SYMBOLGRAPH_OPT 2>&1
GENERATOR_EXIT=$?
set -e

# Save exit code for downstream scripts (e.g., generate-coverage-report.sh)
echo "$GENERATOR_EXIT" > output/generator-exit-code

echo ""
if [ $GENERATOR_EXIT -ne 0 ]; then
    echo "=== Generator exited with code $GENERATOR_EXIT ==="
    if [ "$STRICT" = true ]; then
        echo "STRICT MODE: Failing because generator exited non-zero."
        exit $GENERATOR_EXIT
    fi
    echo "This is expected if the test library includes features beyond current generator support."
    echo ""
fi

echo "=== Output ==="
echo "Output directory: output/"
CS_COUNT=$(find output -name "*.cs" 2>/dev/null | wc -l | tr -d ' ')
SWIFT_COUNT=$(find output -name "*.swift" 2>/dev/null | wc -l | tr -d ' ')
echo "Generated: $CS_COUNT C# files, $SWIFT_COUNT Swift wrapper files"

if [ -f "output/binding-report.json" ]; then
    echo "Binding report: output/binding-report.json"
fi

ls -la output/
