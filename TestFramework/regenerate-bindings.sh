#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Regenerates C# bindings from the built xcframework.
# Requires: build-xcframework.sh to have been run first.
#
# Usage: ./regenerate-bindings.sh

set -e
cd "$(dirname "$0")"

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

# Find the ABI JSON, dylib, and TBD
ABI_JSON=$(find "$SIM_FW_DIR" -name "*.abi.json" | head -1)
DYLIB="$SIM_FW_DIR/$MODULE_NAME"
TBD=$(find "$SIM_FW_DIR" -name "*.tbd" | head -1)

if [ -z "$ABI_JSON" ]; then
    echo "Error: ABI JSON not found in $SIM_FW_DIR"
    exit 1
fi

echo "ABI JSON: $ABI_JSON"
echo "Dylib: $DYLIB"
echo "TBD: $TBD"

# Create output directory
mkdir -p output

# Run the binding generator.
# Note: The generator may crash on certain advanced Swift features (existentials,
# unbound generics, protocol compositions) that are intentionally included in the
# test library to exercise these code paths. A non-zero exit code from the generator
# is reported but does not fail this script — check output/ for partial results.
set +e
dotnet run --project "$PROJECT_ROOT/src/Swift.Bindings/src" -- \
    -a "$ABI_JSON" \
    -d "$DYLIB" \
    -t "$TBD" \
    -o output \
    -l "$MODULE_NAME" 2>&1
GENERATOR_EXIT=$?
set -e

echo ""
if [ $GENERATOR_EXIT -ne 0 ]; then
    echo "=== Generator exited with code $GENERATOR_EXIT ==="
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
