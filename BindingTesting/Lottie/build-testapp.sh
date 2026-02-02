#!/bin/bash
# Build the LottieTestApp

set -e

cd "$(dirname "$0")"

dotnet build LottieTestApp -c Debug
