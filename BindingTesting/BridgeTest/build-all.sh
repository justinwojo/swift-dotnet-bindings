#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Full pipeline: xcframework + bindings + bridge + test app

set -e
cd "$(dirname "$0")"

# Platform check
if [ "$(uname -s)" != "Darwin" ]; then
    echo "Error: This script requires macOS (Darwin)."
    exit 1
fi

echo "=== Step 1: Build xcframework ==="
./build-xcframework.sh

echo ""
echo "=== Step 2: Generate bindings ==="
./regenerate-bindings.sh

echo ""
echo "=== Step 3: Build Swift bridge ==="
./build-bridge.sh

echo ""
echo "=== Step 4: Build test app ==="
./build-testapp.sh

echo ""
echo "=== Build complete ==="
echo "Run ./validate.sh to test on simulator"
