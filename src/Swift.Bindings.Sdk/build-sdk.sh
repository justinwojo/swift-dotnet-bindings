#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Builds the Swift.Bindings.Sdk NuGet package.
# 1. Publishes the generator into the tools/ directory
# 2. Packs the SDK NuGet package

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

echo "=== Building Swift.Bindings.Sdk ==="

# 1. Publish the generator
echo "Publishing generator..."
dotnet publish "$REPO_ROOT/src/Swift.Bindings/src/Swift.Bindings.csproj" \
    -c Release \
    -o "$SCRIPT_DIR/tools/net10.0/any/" \
    --nologo -v quiet

echo "Generator published to $SCRIPT_DIR/tools/net10.0/any/"

# 2. Pack the SDK
echo "Packing SDK NuGet..."
dotnet pack "$SCRIPT_DIR/Swift.Bindings.Sdk.csproj" \
    -c Release \
    -o "$SCRIPT_DIR/bin/nupkg/" \
    --nologo

echo ""
echo "=== SDK package built ==="
ls -la "$SCRIPT_DIR/bin/nupkg/"*.nupkg 2>/dev/null || echo "No .nupkg found"
