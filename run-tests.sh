#!/bin/bash
# Run all tests with proper dotnet host path workaround

set -e

cd "$(dirname "$0")"

DOTNET_PATH=$(which dotnet)

echo "=== Running Unit Tests ==="
dotnet test src/Swift.Bindings/tests/UnitTests -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

echo ""
echo "=== Running Integration Tests ==="
dotnet test src/Swift.Bindings/tests/IntegrationTests -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"

echo ""
echo "=== Running Runtime Tests ==="
dotnet test src/Swift.Runtime/tests -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"
