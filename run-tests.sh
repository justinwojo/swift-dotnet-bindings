#!/bin/bash
# run-tests.sh — thin wrapper over Nuke (preserved for compatibility)
# Original script: run-tests.sh.original
exec nuke test "$@"
