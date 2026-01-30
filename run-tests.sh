#!/bin/bash
# Run unit tests with proper dotnet host path workaround

set -e

cd "$(dirname "$0")"

DOTNET_PATH=$(which dotnet)
dotnet test src/Swift.Bindings/tests/UnitTests -c Debug -- RunConfiguration.DotNetHostPath="$DOTNET_PATH"
