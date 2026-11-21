#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Load safety (P1): prints, space-separated, every OS-resident dylib a linked wrapper slice loads
# strongly in a way a symbol missing from an older OS would fail. Prints nothing when the slice is safe.
#
# A /System/ load must be LC_LOAD_WEAK_DYLIB, except one case the linker forces: an umbrella that
# re-exports a library only its allowed clients may link (SwiftUI re-exports SwiftUICore). A symbol
# of that library binds through the umbrella, and ld keeps the umbrella's load strong whatever
# -weak_framework asks, while -weak_framework still makes every import from it weak, so a missing
# symbol binds to null. Such a load is safe only when every import from it is weak and the SDK's stub
# for it re-exports a library whose stub lists allowable-clients. The generator applies the same rule
# (OsFrameworkWeakLinking.UnsafeStrongLoads) to the slices it links.
#
# Args:
#   $1  the linked binary
#   $2  the SDK path it was linked against
# SWIFTBINDINGS_OTOOL and SWIFTBINDINGS_DYLD_INFO name the tools (default otool, dyld_info); the unit tests point them at stubs.
set -u
binary="$1"
sdk="${2%/}"
otool_tool="${SWIFTBINDINGS_OTOOL:-otool}"
dyld_info_tool="${SWIFTBINDINGS_DYLD_INFO:-dyld_info}"

# A read that fails or shows nothing is an error, never a clean slice.
loads=$("$otool_tool" -l "$binary") || { echo "otool -l failed on $binary" >&2; exit 1; }
if ! printf '%s\n' "$loads" | grep -q 'cmd LC_'; then
    echo "otool -l shows no load commands for $binary" >&2
    exit 1
fi
strong=$(printf '%s\n' "$loads" | awk '$1=="cmd" {c=$2} $1=="name" && c!="LC_LOAD_WEAK_DYLIB" && $2 ~ /^\/System\// {print $2}' | sort -u)
[ -z "$strong" ] && exit 0
imports=$("$dyld_info_tool" -imports "$binary") || { echo "dyld_info -imports failed on $binary" >&2; exit 1; }
if ! printf '%s\n' "$imports" | grep -q -- '-imports:'; then
    echo "dyld_info -imports shows no import table for $binary" >&2
    exit 1
fi

unsafe=""
for load in $strong; do
    leaf=$(basename "$load")
    leaf="${leaf%%.*}"
    # A strong import from the load is never safe.
    if printf '%s\n' "$imports" | grep -F "(from $leaf)" | grep -vqF "[weak-import]"; then
        unsafe="$unsafe $load"
        continue
    fi
    stub="$sdk$load.tbd"
    restricted=""
    if [ -f "$stub" ]; then
        for reexported in $(awk '/^reexported-libraries:/ {f=1; next} f && /^[^ \t-]/ {f=0} f' "$stub" | grep -o "/[^' ,]*" ); do
            if [ -f "$sdk$reexported.tbd" ] && grep -q '^allowable-clients:' "$sdk$reexported.tbd"; then
                restricted=1
                break
            fi
        done
    fi
    [ -n "$restricted" ] || unsafe="$unsafe $load"
done
printf '%s' "${unsafe# }"
