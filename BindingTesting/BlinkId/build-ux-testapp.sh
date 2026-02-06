#!/bin/bash
# Build the BlinkIDUXTestApp

set -e

cd "$(dirname "$0")"

dotnet build BlinkIDUXTestApp -c Debug
