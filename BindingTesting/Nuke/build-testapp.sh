#!/bin/bash
# Build the NukeTestApp

set -e

cd "$(dirname "$0")"

dotnet build NukeTestApp -c Debug
