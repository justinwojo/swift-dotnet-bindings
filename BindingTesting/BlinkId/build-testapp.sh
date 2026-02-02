#!/bin/bash
# Build the BlinkIdTestApp

set -e

cd "$(dirname "$0")"

dotnet build BlinkIdTestApp -c Debug
