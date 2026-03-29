#!/bin/bash
# validate-libraries.sh — thin wrapper over Nuke (preserved for compatibility)
# Original script: validate-libraries.sh.original
#
# Translates common flags to Nuke parameters:
#   --tier N       → --tier N
#   --filter NAME  → --filter NAME
#   --verbose      → --verbose
#   --quick        → --quick
#   --fetch        → --fetch
#   --serial       → --serial
#   --jobs N       → --jobs N
cd "$(dirname "$0")"
exec dotnet nuke validate-libraries "$@"
