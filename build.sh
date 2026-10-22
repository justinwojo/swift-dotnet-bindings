#!/usr/bin/env bash
# Nuke Build entry point
# This file is required by the Nuke CLI.
set -eo pipefail
if [[ "${1:-}" == "--toolchain-profile" ]]; then
    if [[ $# -lt 2 ]]; then
        echo "Usage: ./build.sh --toolchain-profile <profile> [launcher options] -- <nuke target/options>" >&2
        exit 2
    fi
    profile="$2"
    shift 2
    exec python3 "$(dirname "$0")/build/scripts/run-toolchain.py" --profile "$profile" "$@"
fi
dotnet tool restore >/dev/null 2>&1
dotnet run --project build/_build.csproj -- "$@"
