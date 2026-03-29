#!/bin/bash
# pack-all.sh — thin wrapper over Nuke (preserved for compatibility)
# Original script: pack-all.sh.original
#
# Translates flags:
#   --version SEMVER → --version SEMVER
#   --output DIR     → --output-dir DIR

ARGS=()
while [[ $# -gt 0 ]]; do
    case $1 in
        --output)
            ARGS+=("--output-dir" "$2")
            shift 2
            ;;
        *)
            ARGS+=("$1")
            shift
            ;;
    esac
done

cd "$(dirname "$0")"
exec dotnet nuke pack "${ARGS[@]}"
