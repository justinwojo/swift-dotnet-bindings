#!/usr/bin/env bash
# Nuke Build entry point
# This file is required by the Nuke CLI.
set -eo pipefail
dotnet tool restore >/dev/null 2>&1
dotnet run --project build/_build.csproj -- "$@"
