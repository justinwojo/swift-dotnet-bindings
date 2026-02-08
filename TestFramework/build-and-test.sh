#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Convenience script: builds xcframework, regenerates bindings, reports results.
#
# Usage: ./build-and-test.sh [--strict]
#
# Options:
#   --strict    Pass --strict to regenerate-bindings.sh (fail on non-zero generator exit)

set -e
cd "$(dirname "$0")"

REGEN_ARGS=""
while [[ $# -gt 0 ]]; do
    case $1 in
        --strict)
            REGEN_ARGS="--strict"
            shift
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

echo "========================================="
echo " SwiftBindingsTestLib - Build & Generate"
echo "========================================="
echo ""

echo "--- Step 1: Build xcframework ---"
./build-xcframework.sh

echo ""
echo "--- Step 2: Regenerate bindings ---"
./regenerate-bindings.sh $REGEN_ARGS

# Step 3: Build async Swift wrappers (if generated)
ASYNC_SWIFT=$(find output -maxdepth 1 -name "*.swift" ! -name "*.SwiftUIBridge.swift" -type f 2>/dev/null | head -1)
if [ -n "$ASYNC_SWIFT" ]; then
    echo ""
    echo "--- Step 3: Build async Swift wrappers ---"
    ./build-async-wrapper.sh
fi

# Step 4: Build SwiftUI bridge (if generated)
BRIDGE_SWIFT="output/Swift.SwiftBindingsTestLib.SwiftUIBridge.swift"
if [ -f "$BRIDGE_SWIFT" ]; then
    echo ""
    echo "--- Step 4: Build SwiftUI bridge ---"
    ./build-bridge.sh
fi

echo ""
echo "========================================="
echo " Results"
echo "========================================="
echo ""

# Show ABI summary
ABI_JSON=$(find .build -name "*.abi.json" | head -1)
if [ -n "$ABI_JSON" ]; then
    python3 -c "
import json
from collections import Counter
with open('$ABI_JSON') as f:
    abi = json.load(f)
root = abi.get('ABIRoot', abi)
children = root.get('children', [])
kinds = Counter(c.get('declKind', '?') for c in children)
print('ABI summary:')
for k in ['Struct', 'Class', 'Enum', 'Protocol', 'Func']:
    if k in kinds:
        print(f'  {k}s: {kinds[k]}')
print(f'  Total declarations: {len(children)}')
" 2>/dev/null || echo "  (install python3 to view ABI summary)"
fi

echo ""

# Show binding report if it exists
if [ -f "output/binding-report.json" ]; then
    echo "Binding report:"
    python3 -c "
import json
with open('output/binding-report.json') as f:
    report = json.load(f)
if isinstance(report, dict):
    for key, value in report.items():
        if isinstance(value, (int, str, float, bool)):
            print(f'  {key}: {value}')
        elif isinstance(value, list):
            print(f'  {key}: {len(value)} items')
" 2>/dev/null || echo "  (install python3 to view report summary)"
    echo ""
    echo "Full report: output/binding-report.json"
else
    echo "No binding-report.json found (generator may have crashed — see output above)"
fi

echo ""
# Count generated files
CS_COUNT=$(find output -name "*.cs" 2>/dev/null | wc -l | tr -d ' ')
SWIFT_COUNT=$(find output -name "*.swift" 2>/dev/null | wc -l | tr -d ' ')
echo "Generated files: $CS_COUNT C# files, $SWIFT_COUNT Swift wrapper files"
echo ""
echo "========================================="
echo " Done"
echo "========================================="
