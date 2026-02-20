#!/bin/bash
# Compares current generator output against stored golden files.
# Exit 1 if any golden file differs (regression detected).

set -e

cd "$(dirname "$0")/.."

PROJ="src/Swift.Bindings/src/Swift.Bindings.csproj"
GOLDEN_DIR="golden"
FAILURES=0

# Build the generator once
echo "Building generator..."
dotnet build "$PROJ" -c Debug --nologo -q 2>/dev/null

generate_and_check() {
    local name="$1"
    local xcframework="$2"
    local golden_file="$GOLDEN_DIR/${name}.cs.golden"

    if [ ! -f "$golden_file" ]; then
        echo "  FAIL: $golden_file not found (run update-golden-files.sh first)"
        FAILURES=$((FAILURES + 1))
        return 0
    fi

    local tmpdir
    tmpdir=$(mktemp -d)

    echo "  Checking $name..."
    # Tolerate wrapper compilation failures — the .cs file is still generated
    dotnet run --project "$PROJ" --no-build -- \
        --xcframework "$xcframework" \
        -o "$tmpdir/" \
        2>/dev/null || true

    local csfile
    csfile=$(ls "$tmpdir"/Swift.*.cs 2>/dev/null | head -1)
    if [ -z "$csfile" ]; then
        echo "    ERROR: No Swift.*.cs found for $name"
        rm -rf "$tmpdir"
        FAILURES=$((FAILURES + 1))
        return 0
    fi

    if diff -u "$golden_file" "$csfile" > /dev/null 2>&1; then
        echo "    OK"
    else
        echo "    DIFF: $name differs from golden file"
        diff -u "$golden_file" "$csfile" | head -40
        echo "    ..."
        FAILURES=$((FAILURES + 1))
    fi

    rm -rf "$tmpdir"
}

echo ""
echo "=== Checking golden files ==="

generate_and_check "SwiftBindingsTestLib" "TestFramework/.build/SwiftBindingsTestLib.xcframework"

echo ""
if [ $FAILURES -gt 0 ]; then
    echo "FAILED: $FAILURES golden file(s) differ. Run golden/update-golden-files.sh to update."
    exit 1
else
    echo "All golden files match."
fi
