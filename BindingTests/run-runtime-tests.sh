#!/bin/bash
# run-runtime-tests.sh — thin wrapper over Nuke (preserved for compatibility)
# Original script: run-runtime-tests.sh.original
#
# Translates flags:
#   --platform simulator → nuke runtime-tests-simulator
#   --platform device    → nuke runtime-tests-device
#   --platform macos     → nuke runtime-tests-macos
#   --skip-regen         → --skip-regen
#   --skip-build         → --skip-build
#   --timeout N          → --timeout N
#   --class NAME         → --class-filter NAME
#   --flake-detect       → --flake-detect
#   --device-udid UDID   → --device-udid UDID

cd "$(dirname "$0")/.."

# Parse --platform to select the right Nuke target
TARGET="runtime-tests-simulator"
NUKE_ARGS=()

while [[ $# -gt 0 ]]; do
    case $1 in
        --platform)
            case "$2" in
                simulator) TARGET="runtime-tests-simulator" ;;
                device)    TARGET="runtime-tests-device" ;;
                macos)     TARGET="runtime-tests-macos" ;;
                *)         echo "Unknown platform: $2"; exit 1 ;;
            esac
            shift 2
            ;;
        --class)
            NUKE_ARGS+=("--class-filter" "$2")
            shift 2
            ;;
        *)
            NUKE_ARGS+=("$1")
            shift
            ;;
    esac
done

exec nuke "$TARGET" "${NUKE_ARGS[@]}"
