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
DEP_MODULE_NAME="SwiftBindingsTestLibDependency"
PROJECT_ROOT=".."
XCFW_DIR=".build/$MODULE_NAME.xcframework"
DEP_XCFW_DIR=".build/$DEP_MODULE_NAME.xcframework"

if [ ! -d "$XCFW_DIR" ]; then
    echo "Error: xcframework not found at $XCFW_DIR"
    echo "Run ./build-xcframework.sh first."
    exit 1
fi

echo "=== Regenerating bindings for $MODULE_NAME ==="
echo "xcframework: $XCFW_DIR"

# Clean and create output directory (remove stale files from prior runs)
rm -rf output
mkdir -p output

# Run the binding generator in --xcframework mode.
# This auto-discovers ABI JSON, dylib, TBD, and swiftinterface from the xcframework.
# Note: The generator may crash on certain advanced Swift features (existentials,
# unbound generics, protocol compositions) that are intentionally included in the
# test library to exercise these code paths. A non-zero exit code from the generator
# is reported but does not fail this script — check output/ for partial results.
set +e

SYMBOLGRAPH_OPT=""
SYMBOLGRAPH_DIR=".build/symbolgraph"
if [ -d "$SYMBOLGRAPH_DIR" ]; then
    SG_COUNT=$(find "$SYMBOLGRAPH_DIR" -name "*.symbols.json" 2>/dev/null | wc -l | tr -d ' ')
    if [ "$SG_COUNT" -gt 0 ]; then
        SYMBOLGRAPH_OPT="--symbolgraph $SYMBOLGRAPH_DIR"
        echo "Symbol graph: $SYMBOLGRAPH_DIR ($SG_COUNT files)"
    fi
fi

DEP_FW_OPT=""
if [ -d "$DEP_XCFW_DIR" ]; then
    echo "Dependency xcframework: $DEP_XCFW_DIR"
    DEP_FW_OPT="--framework-dependency $DEP_XCFW_DIR"
fi

dotnet run --project "$PROJECT_ROOT/src/Swift.Bindings/src" -- \
    --xcframework "$XCFW_DIR" \
    -o output \
    --async-library SwiftBindings \
    $SYMBOLGRAPH_OPT \
    $DEP_FW_OPT 2>&1
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

# Generate bindings for the dependency module (if present).
# These are needed so the main module's cross-module type references compile.
if [ -d "$DEP_XCFW_DIR" ]; then
    echo "=== Generating dependency bindings for $DEP_MODULE_NAME ==="
    mkdir -p output/dep
    set +e
    dotnet run --project "$PROJECT_ROOT/src/Swift.Bindings/src" -- \
        --xcframework "$DEP_XCFW_DIR" \
        -o output/dep 2>&1
    DEP_EXIT=$?
    set -e
    if [ $DEP_EXIT -ne 0 ]; then
        echo "Dependency bindings generation exited with code $DEP_EXIT (non-fatal)"
    fi
    # Move the dependency .cs file alongside the main bindings
    if [ -f "output/dep/$DEP_MODULE_NAME.cs" ]; then
        mv "output/dep/$DEP_MODULE_NAME.cs" "output/$DEP_MODULE_NAME.cs"
        echo "Dependency bindings: output/$DEP_MODULE_NAME.cs"
    fi
    # Preserve the dependency wrapper xcframework for runtime linking
    DEP_WRAPPER_XCF="output/dep/${DEP_MODULE_NAME}SwiftBindings.xcframework"
    if [ -d "$DEP_WRAPPER_XCF" ]; then
        rm -rf "output/${DEP_MODULE_NAME}SwiftBindings.xcframework"
        mv "$DEP_WRAPPER_XCF" "output/${DEP_MODULE_NAME}SwiftBindings.xcframework"
        echo "Dependency wrapper: output/${DEP_MODULE_NAME}SwiftBindings.xcframework"
    fi
    rm -rf output/dep
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
