#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Generates bridge coverage metrics for tracked SwiftUI libraries.
# Reads bridge-corpus/manifest.json, runs the generator on each library,
# extracts BridgeSummary from binding-report.json, optionally runs
# swiftc -typecheck, and writes bridge-corpus/coverage-report.json.
#
# Usage: ./generate-bridge-coverage.sh [--typecheck] [--filter NAME]
#
# Requires:
#   - Generator built (./build.sh from repo root)
#   - For validation libraries: xcframeworks fetched (scripts/fetch-libraries.sh)
#   - For BindingTests: output/ populated (./regenerate-bindings.sh)

set -e
cd "$(dirname "$0")"

REPO_ROOT="$(cd .. && pwd)"
MANIFEST="bridge-corpus/manifest.json"
OUTPUT_DIR="bridge-corpus"
REPORT_FILE="$OUTPUT_DIR/coverage-report.json"
GENERATOR_PROJECT="$REPO_ROOT/src/Swift.Bindings/src"
DO_TYPECHECK=false
FILTER=""

while [[ $# -gt 0 ]]; do
    case $1 in
        --typecheck) DO_TYPECHECK=true; shift ;;
        --filter) FILTER="$2"; shift 2 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

if [ ! -f "$MANIFEST" ]; then
    echo "Error: $MANIFEST not found."
    exit 1
fi

# Build generator if needed
GENERATOR_DLL=$(find "$REPO_ROOT/src/Swift.Bindings/src/bin" -name "Swift.Bindings.dll" 2>/dev/null | head -1)
if [ -z "$GENERATOR_DLL" ]; then
    echo "Building generator..."
    (cd "$REPO_ROOT" && ./build.sh > /dev/null 2>&1)
    GENERATOR_DLL=$(find "$REPO_ROOT/src/Swift.Bindings/src/bin" -name "Swift.Bindings.dll" 2>/dev/null | head -1)
fi

# Parse manifest
LIB_COUNT=$(python3 -c "import json; m=json.load(open('$MANIFEST')); print(len(m['libraries']))")
echo "Bridge coverage: $LIB_COUNT libraries in manifest"

RESULTS="["
FIRST=true

for i in $(seq 0 $((LIB_COUNT - 1))); do
    NAME=$(python3 -c "import json; m=json.load(open('$MANIFEST')); print(m['libraries'][$i]['name'])")
    SOURCE=$(python3 -c "import json; m=json.load(open('$MANIFEST')); print(m['libraries'][$i]['source'])")
    EXPECTED_VIEWS=$(python3 -c "import json; m=json.load(open('$MANIFEST')); print(m['libraries'][$i].get('views', 0))")
    RUNTIME_VALIDATED=$(python3 -c "import json; m=json.load(open('$MANIFEST')); print(str(m['libraries'][$i].get('runtime_validated', False)).lower())")

    # Apply filter
    if [ -n "$FILTER" ] && [ "$NAME" != "$FILTER" ]; then
        continue
    fi

    echo -n "  $NAME ($SOURCE)... "

    BRIDGE_SUMMARY=""
    TYPECHECK_PASS="null"

    if [ "$SOURCE" = "BindingTests" ]; then
        # Use existing output from BindingTests
        REPORT="output/binding-report.json"
        if [ -f "$REPORT" ]; then
            BRIDGE_SUMMARY=$(python3 -c "
import json
r = json.load(open('$REPORT'))
bs = r.get('bridgeSummary') or r.get('BridgeSummary')
if bs:
    print(json.dumps(bs))
else:
    print('null')
" 2>/dev/null || echo "null")
        fi
    else
        # Validation library — run generator
        XCFW=""
        if [ -d "$REPO_ROOT/.libraries/$NAME" ]; then
            XCFW=$(find "$REPO_ROOT/.libraries/$NAME" -name "*.xcframework" -maxdepth 1 2>/dev/null | head -1)
        fi

        if [ -z "$XCFW" ]; then
            echo "SKIP (xcframework not found)"
            if [ "$FIRST" = true ]; then FIRST=false; else RESULTS+=","; fi
            RESULTS+=$(cat <<EOF
{
    "name": "$NAME",
    "source": "$SOURCE",
    "status": "skipped",
    "reason": "xcframework not found"
}
EOF
)
            continue
        fi

        TMPDIR=$(mktemp -d)
        GEN_OUTPUT=$(dotnet "$GENERATOR_DLL" --skip-wrapper-compilation --xcframework "$XCFW" -o "$TMPDIR" 2>&1) || true

        REPORT="$TMPDIR/binding-report.json"
        if [ -f "$REPORT" ]; then
            BRIDGE_SUMMARY=$(python3 -c "
import json
r = json.load(open('$REPORT'))
bs = r.get('bridgeSummary') or r.get('BridgeSummary')
if bs:
    print(json.dumps(bs))
else:
    print('null')
" 2>/dev/null || echo "null")

            # Optional: swiftc -typecheck
            if $DO_TYPECHECK; then
                BRIDGE_SWIFT=$(find "$TMPDIR" -name "*.SwiftUIBridge.swift" 2>/dev/null | head -1)
                if [ -n "$BRIDGE_SWIFT" ]; then
                    if xcrun swiftc -typecheck "$BRIDGE_SWIFT" -sdk "$(xcrun --sdk iphonesimulator --show-sdk-path)" -target arm64-apple-ios18.0-simulator 2>/dev/null; then
                        TYPECHECK_PASS="true"
                    else
                        TYPECHECK_PASS="false"
                    fi
                fi
            fi
        fi

        rm -rf "$TMPDIR"
    fi

    if [ "$BRIDGE_SUMMARY" = "null" ] || [ -z "$BRIDGE_SUMMARY" ]; then
        echo "no bridge data"
        if [ "$FIRST" = true ]; then FIRST=false; else RESULTS+=","; fi
        RESULTS+=$(cat <<EOF
{
    "name": "$NAME",
    "source": "$SOURCE",
    "status": "no_bridge_data",
    "expected_views": $EXPECTED_VIEWS,
    "runtime_validated": $RUNTIME_VALIDATED
}
EOF
)
    else
        GENERATED=$(python3 -c "import json; d=json.loads('$BRIDGE_SUMMARY'); print(d.get('generated') or d.get('Generated', 0))")
        TOTAL=$(python3 -c "import json; d=json.loads('$BRIDGE_SUMMARY'); print(d.get('totalViews') or d.get('TotalViews', 0))")
        echo "OK ($GENERATED/$TOTAL generated)"

        if [ "$FIRST" = true ]; then FIRST=false; else RESULTS+=","; fi
        RESULTS+=$(cat <<EOF
{
    "name": "$NAME",
    "source": "$SOURCE",
    "status": "ok",
    "expected_views": $EXPECTED_VIEWS,
    "runtime_validated": $RUNTIME_VALIDATED,
    "bridge_summary": $BRIDGE_SUMMARY,
    "typecheck_pass": $TYPECHECK_PASS
}
EOF
)
    fi
done

RESULTS+="]"

# Write report
python3 -c "
import json, sys
results = json.loads(sys.argv[1])
report = {
    'generated_at': '$(date -u +%Y-%m-%dT%H:%M:%SZ)',
    'libraries': results
}
with open('$REPORT_FILE', 'w') as f:
    json.dump(report, f, indent=2)
" "$RESULTS"

echo ""
echo "Coverage report written to $REPORT_FILE"
