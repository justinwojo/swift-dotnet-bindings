#!/bin/bash
# Run all tests with proper dotnet host path workaround

set -e

cd "$(dirname "$0")"

DOTNET_PATH=$(which dotnet)

# Build everything first (integration tests require cmake pre-build step)
echo "=== Building Projects ==="
./build.sh

echo ""
echo "=== Running Unit Tests ==="
dotnet test src/Swift.Bindings/tests/UnitTests --no-build -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

echo ""
echo "=== Running Integration Tests ==="
# Use --no-build to avoid cmake reconfiguration issues
dotnet test src/Swift.Bindings/tests/IntegrationTests --no-build -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

echo ""
echo "=== Running Runtime Tests ==="
dotnet test src/Swift.Runtime/tests --no-build -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"
