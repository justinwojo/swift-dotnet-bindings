#!/bin/bash
# Full Step 3 pipeline: generator coverage + bridge build + test app build
#
# Runs both test layers:
#   1. Generator coverage: run binding generator on BlinkIDUX
#   2. Runtime bridge: build Swift bridge + .NET test app

set -e

cd "$(dirname "$0")"

echo "=== Generator coverage: BlinkIDUX ==="
./regenerate-ux-bindings.sh

echo ""
echo "=== Building Swift bridge ==="
./build-bridge.sh

echo ""
echo "=== Building UX test app ==="
./build-ux-testapp.sh

echo ""
echo "=== Step 3 build complete ==="
echo "Run ./validate-bridge.sh to test on simulator"
