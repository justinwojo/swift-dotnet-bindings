#!/bin/bash
# build-and-test.sh — thin wrapper over Nuke (preserved for compatibility)
# Original script: build-and-test.sh.original
#
# Translates flags:
#   --strict → --strict (passed through to Nuke)
cd "$(dirname "$0")/.."
exec nuke binding-tests "$@"
