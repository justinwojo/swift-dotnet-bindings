#!/bin/bash
# Regenerates golden files from current generator output.
# Run this after intentional generator changes to update the baseline.
# Exits non-zero if generation fails to produce output.

set -e

cd "$(dirname "$0")/../.."

PROJ="src/Swift.Bindings/src/Swift.Bindings.csproj"
GOLDEN_DIR="TestFramework/golden"
FAILURES=0

# Build the generator once
echo "Building generator..."
dotnet build "$PROJ" -c Debug --nologo -q 2>/dev/null

generate_for_lib() {
    local name="$1"
    local xcframework="$2"

    local tmpdir
    tmpdir=$(mktemp -d)

    echo "  Generating $name..."
    # Tolerate wrapper compilation failures — the .cs file is still generated
    dotnet run --project "$PROJ" --no-build -- \
        --xcframework "$xcframework" \
        -o "$tmpdir/" \
        2>/dev/null || true

    local csfile
    csfile=$(ls "$tmpdir"/*.cs 2>/dev/null | grep -v '\.Wrappers\.cs' | grep -v '\.SwiftUIBridge\.cs' | head -1)
    if [ -z "$csfile" ]; then
        echo "    ERROR: No binding .cs file found for $name — golden file NOT updated"
        rm -rf "$tmpdir"
        FAILURES=$((FAILURES + 1))
        return 0
    fi

    cp "$csfile" "$GOLDEN_DIR/${name}.cs.golden"
    local lines
    lines=$(wc -l < "$csfile" | tr -d ' ')
    echo "    -> ${name}.cs.golden ($lines lines)"
    rm -rf "$tmpdir"
}

echo ""
echo "=== Generating golden files ==="

generate_for_lib "SwiftBindingsTestLib" "TestFramework/.build/SwiftBindingsTestLib.xcframework"

echo ""
if [ $FAILURES -gt 0 ]; then
    echo "WARNING: $FAILURES library(ies) failed to generate. Golden files may be stale."
    exit 1
else
    echo "Done. Golden files updated in $GOLDEN_DIR/"
fi
