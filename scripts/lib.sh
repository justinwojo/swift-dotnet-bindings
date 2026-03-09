#!/bin/bash
# scripts/lib.sh — Shared helpers for validation scripts.
# Source this file: source "$(dirname "$0")/lib.sh"

die() { echo "Error: $*" >&2; exit 1; }

# Manifest path — defaults to repo root
: "${MANIFEST:=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/validation-libraries.json}"

# --- Colors ---
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
DIM='\033[2m'
NC='\033[0m'

# --- Manifest Helpers ---

manifest_lib_count() {
    python3 -c "import json; print(len(json.load(open('$MANIFEST'))['libraries']))"
}

manifest_lib_field() {
    # manifest_lib_field <lib_idx> <field> [default]
    python3 -c "
import json
lib = json.load(open('$MANIFEST'))['libraries'][$1]
val = lib.get('$2')
if val is None: print('${3:-}')
elif isinstance(val, bool): print('true' if val else 'false')
else: print(val)
"
}

manifest_product_count() {
    python3 -c "import json; print(len(json.load(open('$MANIFEST'))['libraries'][$1]['products']))"
}

manifest_product_field() {
    # manifest_product_field <lib_idx> <prod_idx> <field> [default]
    python3 -c "
import json
prod = json.load(open('$MANIFEST'))['libraries'][$1]['products'][$2]
val = prod.get('$3')
if val is None: print('${4:-}')
elif isinstance(val, bool): print('true' if val else 'false')
else: print(val)
"
}

manifest_build_settings() {
    # Print KEY=VALUE lines from buildSettings for library at index $1
    python3 -c "
import json
lib = json.load(open('$MANIFEST'))['libraries'][$1]
for k, v in lib.get('buildSettings', {}).items():
    print(f'{k}={v}')
"
}

# Expand manifest to flat list of validation targets.
# Output: one line per target: "framework|library_name|xcfw_path|mode|knownErrors|tier|platform"
# Libraries with "platforms": ["ios", "macos", "tvos"] emit one target per platform.
# The default (no platforms field) emits a single iOS target with platform="ios".
manifest_expand_targets() {
    local libraries_dir="${1:-.libraries}"
    python3 -c "
import json
libs = json.load(open('$MANIFEST'))['libraries']
for lib in libs:
    name = lib['name']
    mode = lib['mode']
    tier = lib.get('tier', 1)
    platforms = lib.get('platforms', ['ios'])
    for prod in lib['products']:
        fw = prod['framework']
        known = prod.get('knownErrors', 0)
        for plat in platforms:
            # Default iOS target uses bare framework name for backward compatibility
            target_name = fw if plat == 'ios' else f'{fw}@{plat}'
            print(f'{target_name}|{name}|$libraries_dir/{name}/{fw}.xcframework|{mode}|{known}|{tier}|{plat}')
"
}

matches_filter() {
    [[ -z "${FILTER:-}" ]] && return 0
    echo "$1" | grep -qi "$FILTER"
}

matches_tier() {
    [[ "${TIER:-all}" == "all" ]] && return 0
    [[ "$1" == "${TIER:-1}" ]]
}
