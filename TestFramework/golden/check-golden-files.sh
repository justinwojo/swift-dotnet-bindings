#!/bin/bash
# Compares current generator output against stored golden files.
# Exit 1 if any golden file differs (regression detected).

set -e

cd "$(dirname "$0")/../.."

PROJ="src/Swift.Bindings/src/Swift.Bindings.csproj"
GOLDEN_DIR="TestFramework/golden"
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
    csfile=$(ls "$tmpdir"/*.cs 2>/dev/null | grep -v '\.Wrappers\.cs' | grep -v '\.SwiftUIBridge\.cs' | head -1)
    if [ -z "$csfile" ]; then
        echo "    ERROR: No binding .cs file found for $name"
        rm -rf "$tmpdir"
        FAILURES=$((FAILURES + 1))
        return 0
    fi

    if diff -u "$golden_file" "$csfile" > /dev/null 2>&1; then
        echo "    C#: OK"
    else
        echo "    DIFF: $name C# bindings differ from golden file"
        diff -u "$golden_file" "$csfile" | head -40
        echo "    ..."
        FAILURES=$((FAILURES + 1))
    fi

    # Check Swift wrapper golden file
    local swift_golden="$GOLDEN_DIR/${name}.swift.golden"
    if [ -f "$swift_golden" ]; then
        local swiftfile
        swiftfile=$(ls "$tmpdir"/${name}.swift 2>/dev/null | head -1)
        if [ -z "$swiftfile" ]; then
            echo "    ERROR: No Swift wrapper file found for $name"
            FAILURES=$((FAILURES + 1))
        elif diff -u "$swift_golden" "$swiftfile" > /dev/null 2>&1; then
            echo "    Swift: OK"
        else
            echo "    DIFF: $name Swift wrapper differs from golden file"
            diff -u "$swift_golden" "$swiftfile" | head -40
            echo "    ..."
            FAILURES=$((FAILURES + 1))
        fi
    fi

    rm -rf "$tmpdir"
}

echo ""
echo "=== Checking golden files ==="

generate_and_check "SwiftBindingsTestLib" "TestFramework/.build/SwiftBindingsTestLib.xcframework"

echo ""
if [ $FAILURES -gt 0 ]; then
    echo "FAILED: $FAILURES golden file(s) differ. Run TestFramework/golden/update-golden-files.sh to update."
    exit 1
else
    echo "All golden files match."
fi
