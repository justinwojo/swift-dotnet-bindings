#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Builds a universal SwiftBindings.xcframework with simulator + device slices.
# Uses the generator in --xcframework mode with --wrapper-architectures all to
# leverage the SwiftWrapperPostProcessor (strips broken wrapper code).
# Then renames the wrapper binary from the module-unique name back to "SwiftBindings"
# to match the DllImport library name used in generated C# bindings.
#
# Requires:
#   - .build/SwiftBindingsTestLib.xcframework/ with both ios-arm64 and ios-arm64-simulator slices
#   - Generator at ../src/Swift.Bindings/src/
#
# Usage: ./build-wrapper-device.sh

set -e
cd "$(dirname "$0")"

XCFW=".build/SwiftBindingsTestLib.xcframework"

if [ ! -d "$XCFW/ios-arm64" ]; then
    echo "ERROR: Device slice not found in $XCFW"
    echo "Run ./build-xcframework.sh --include-device first."
    exit 1
fi

echo "=== Regenerating bindings with dual-architecture wrapper ==="

# Run generator in --xcframework mode with both wrapper slices
dotnet run --project ../src/Swift.Bindings/src -- \
    --xcframework "$XCFW" \
    -o output \
    --async-library SwiftBindings \
    --wrapper-architectures all \
    --symbolgraph .build/symbolgraph 2>&1 | tail -15

# The generator creates SwiftBindingsTestLibSwiftBindings.xcframework (module-unique name)
# but DllImport references "SwiftBindings". Rename the binaries to match.
GEN_XCFW="output/SwiftBindingsTestLibSwiftBindings.xcframework"
FINAL_XCFW="output/SwiftBindings.xcframework"

if [ ! -d "$GEN_XCFW" ]; then
    echo "ERROR: Generator did not produce $GEN_XCFW"
    exit 1
fi

echo ""
echo "=== Creating SwiftBindings.xcframework with correct binary names ==="

# Save sim slice from the generator output (has correct post-processed code)
SIM_SRC="$GEN_XCFW/ios-arm64-simulator/SwiftBindingsTestLibSwiftBindings.framework"
DEV_SRC="$GEN_XCFW/ios-arm64/SwiftBindingsTestLibSwiftBindings.framework"

WORK_DIR=".build/wrapper-rename"
rm -rf "$WORK_DIR"
mkdir -p "$WORK_DIR/sim/SwiftBindings.framework" "$WORK_DIR/dev/SwiftBindings.framework"

# Copy and rename simulator binary
cp "$SIM_SRC/SwiftBindingsTestLibSwiftBindings" "$WORK_DIR/sim/SwiftBindings.framework/SwiftBindings"
cp "$SIM_SRC/Info.plist" "$WORK_DIR/sim/SwiftBindings.framework/Info.plist"

# Copy and rename device binary
cp "$DEV_SRC/SwiftBindingsTestLibSwiftBindings" "$WORK_DIR/dev/SwiftBindings.framework/SwiftBindings"
install_name_tool -id @rpath/SwiftBindings.framework/SwiftBindings "$WORK_DIR/dev/SwiftBindings.framework/SwiftBindings" 2>/dev/null || true
cp "$DEV_SRC/Info.plist" "$WORK_DIR/dev/SwiftBindings.framework/Info.plist"

# Fix Info.plist executable names
for plist in "$WORK_DIR/sim/SwiftBindings.framework/Info.plist" "$WORK_DIR/dev/SwiftBindings.framework/Info.plist"; do
    sed -i '' 's/SwiftBindingsTestLibSwiftBindings/SwiftBindings/g' "$plist"
done

# Create universal xcframework
rm -rf "$FINAL_XCFW"
xcodebuild -create-xcframework \
    -framework "$WORK_DIR/sim/SwiftBindings.framework" \
    -framework "$WORK_DIR/dev/SwiftBindings.framework" \
    -output "$FINAL_XCFW"

# Cleanup
rm -rf "$GEN_XCFW" "$WORK_DIR"

echo ""
echo "=== Wrapper Build Complete ==="
echo "xcframework: $FINAL_XCFW"
ls -la "$FINAL_XCFW/"
