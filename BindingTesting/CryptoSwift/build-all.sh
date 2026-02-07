#!/bin/bash
# Full build: regenerate bindings, build Swift wrapper, test app

set -e

cd "$(dirname "$0")"

echo "=== Regenerating bindings ==="
./regenerate-bindings.sh

echo ""
echo "=== Building Swift wrapper ==="
./build-swift-wrapper.sh

echo ""
echo "=== Building test app ==="
./build-testapp.sh

echo ""
echo "=== Build complete ==="
echo "Run ./validate-sim.sh to test on simulator"
