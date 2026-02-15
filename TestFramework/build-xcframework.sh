#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Builds the SwiftBindingsTestLib as an xcframework for iOS Simulator arm64.
# Optionally includes an iOS device (arm64) slice for physical device deployment.
# Produces: .build/SwiftBindingsTestLib.xcframework/
#
# Usage: ./build-xcframework.sh [--include-device]
#
# Options:
#   --include-device    Also build for ios-arm64 (physical device)

set -e
cd "$(dirname "$0")"

INCLUDE_DEVICE=false
while [[ $# -gt 0 ]]; do
    case $1 in
        --include-device)
            INCLUDE_DEVICE=true
            shift
            ;;
        *)
            echo "Unknown option: $1"
            echo "Usage: ./build-xcframework.sh [--include-device]"
            exit 1
            ;;
    esac
done

MODULE_NAME="SwiftBindingsTestLib"
BUILD_DIR=".build"
SIM_BUILD_DIR="$BUILD_DIR/ios-simulator"
FRAMEWORK_DIR="$SIM_BUILD_DIR/$MODULE_NAME.framework"
DEVICE_BUILD_DIR="$BUILD_DIR/ios-device"
DEVICE_FRAMEWORK_DIR="$DEVICE_BUILD_DIR/$MODULE_NAME.framework"
XCFRAMEWORK_DIR="$BUILD_DIR/$MODULE_NAME.xcframework"

# SDK paths
SIM_SDK=$(xcrun --sdk iphonesimulator --show-sdk-path)
SIM_TARGET="arm64-apple-ios15.0-simulator"

echo "=== Building $MODULE_NAME ==="
echo "Simulator target: $SIM_TARGET"
echo "Simulator SDK: $SIM_SDK"
[ "$INCLUDE_DEVICE" = true ] && echo "Device build: ENABLED"

# Clean previous build
rm -rf "$BUILD_DIR"
mkdir -p "$SIM_BUILD_DIR"
mkdir -p "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule"

# Collect Swift source files (excluding disabled directories and files)
# This matches the exclusions in Package.swift
SWIFT_FILES=$(find Sources/SwiftBindingsTestLib \
    -type d -name '*.disabled' -prune -o \
    -type d -name 'Foundation' -prune -o \
    -name '*.swift' -type f -print \
    | grep -v 'Closures/Autoclosures\.swift$' \
    | grep -v 'UnsafeTypes/Span\.swift$' \
    | grep -v 'UnsafeTypes/PointerGenerics\.swift$')
FILE_COUNT=$(echo "$SWIFT_FILES" | wc -l | tr -d ' ')
echo "Compiling $FILE_COUNT Swift source files..."

# Compile simulator slice with library evolution enabled
echo ""
echo "--- Compiling simulator slice ---"
xcrun swiftc \
    -target "$SIM_TARGET" \
    -sdk "$SIM_SDK" \
    -emit-module \
    -emit-library \
    -enable-library-evolution \
    -emit-module-interface \
    -module-name "$MODULE_NAME" \
    -Xlinker -install_name -Xlinker "@rpath/$MODULE_NAME.framework/$MODULE_NAME" \
    -o "$FRAMEWORK_DIR/$MODULE_NAME" \
    -emit-module-path "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios-simulator.swiftmodule" \
    -emit-module-interface-path "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios-simulator.swiftinterface" \
    $SWIFT_FILES

# Copy private swiftinterface (same as public for our purposes)
cp "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios-simulator.swiftinterface" \
   "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios-simulator.private.swiftinterface"

echo "=== Extracting Symbol Graph ==="
SYMBOLGRAPH_DIR="$BUILD_DIR/symbolgraph"
if xcrun --find swift-symbolgraph-extract &>/dev/null; then
    mkdir -p "$SYMBOLGRAPH_DIR"
    if xcrun swift-symbolgraph-extract \
        -module-name "$MODULE_NAME" \
        -target "$SIM_TARGET" \
        -sdk "$SIM_SDK" \
        -I "$SIM_BUILD_DIR" \
        -F "$SIM_BUILD_DIR" \
        -output-dir "$SYMBOLGRAPH_DIR" \
        -pretty-print 2>&1; then
        SG_COUNT=$(find "$SYMBOLGRAPH_DIR" -name "*.symbols.json" 2>/dev/null | wc -l | tr -d ' ')
        echo "Extracted $SG_COUNT symbol graph files to $SYMBOLGRAPH_DIR"
    else
        echo "WARNING: swift-symbolgraph-extract failed (exit code $?). Doc comments will not be available."
    fi
else
    echo "Warning: swift-symbolgraph-extract not found. Doc comments will not be available."
fi

echo "=== Generating TBD (simulator) ==="
xcrun tapi stubify \
    --filetype=tbd-v4 \
    "$FRAMEWORK_DIR/$MODULE_NAME" \
    -o "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/$MODULE_NAME.tbd"

echo "=== Generating ABI JSON (simulator) ==="
xcrun swift-frontend \
    -compile-module-from-interface \
    "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios-simulator.swiftinterface" \
    -target "$SIM_TARGET" \
    -module-name "$MODULE_NAME" \
    -sdk "$SIM_SDK" \
    -emit-abi-descriptor-path "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios-simulator.abi.json"

# Create simulator framework Info.plist
cat > "$FRAMEWORK_DIR/Info.plist" << 'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>SwiftBindingsTestLib</string>
    <key>CFBundleIdentifier</key>
    <string>com.test.SwiftBindingsTestLib</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>SwiftBindingsTestLib</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
</dict>
</plist>
PLIST

# --- Device slice (optional) ---
if [ "$INCLUDE_DEVICE" = true ]; then
    DEVICE_SDK=$(xcrun --sdk iphoneos --show-sdk-path)
    DEVICE_TARGET="arm64-apple-ios15.0"

    echo ""
    echo "--- Compiling device slice ---"
    echo "Device target: $DEVICE_TARGET"
    echo "Device SDK: $DEVICE_SDK"

    mkdir -p "$DEVICE_BUILD_DIR"
    mkdir -p "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule"

    xcrun swiftc \
        -target "$DEVICE_TARGET" \
        -sdk "$DEVICE_SDK" \
        -emit-module \
        -emit-library \
        -enable-library-evolution \
        -emit-module-interface \
        -module-name "$MODULE_NAME" \
        -Xlinker -install_name -Xlinker "@rpath/$MODULE_NAME.framework/$MODULE_NAME" \
        -o "$DEVICE_FRAMEWORK_DIR/$MODULE_NAME" \
        -emit-module-path "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios.swiftmodule" \
        -emit-module-interface-path "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios.swiftinterface" \
        $SWIFT_FILES

    cp "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios.swiftinterface" \
       "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios.private.swiftinterface"

    echo "=== Generating TBD (device) ==="
    xcrun tapi stubify \
        --filetype=tbd-v4 \
        "$DEVICE_FRAMEWORK_DIR/$MODULE_NAME" \
        -o "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/$MODULE_NAME.tbd"

    echo "=== Generating ABI JSON (device) ==="
    xcrun swift-frontend \
        -compile-module-from-interface \
        "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios.swiftinterface" \
        -target "$DEVICE_TARGET" \
        -module-name "$MODULE_NAME" \
        -sdk "$DEVICE_SDK" \
        -emit-abi-descriptor-path "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios.abi.json"

    # Device framework Info.plist
    cat > "$DEVICE_FRAMEWORK_DIR/Info.plist" << 'DPLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>SwiftBindingsTestLib</string>
    <key>CFBundleIdentifier</key>
    <string>com.test.SwiftBindingsTestLib</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>SwiftBindingsTestLib</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
</dict>
</plist>
DPLIST
fi

echo "=== Creating xcframework ==="
rm -rf "$XCFRAMEWORK_DIR"
if [ "$INCLUDE_DEVICE" = true ]; then
    xcodebuild -create-xcframework \
        -framework "$FRAMEWORK_DIR" \
        -framework "$DEVICE_FRAMEWORK_DIR" \
        -output "$XCFRAMEWORK_DIR"
else
    xcodebuild -create-xcframework \
        -framework "$FRAMEWORK_DIR" \
        -output "$XCFRAMEWORK_DIR"
fi

echo ""
echo "=== Build Complete ==="
echo "xcframework: $XCFRAMEWORK_DIR"
[ "$INCLUDE_DEVICE" = true ] && echo "Slices: simulator + device"
echo ""
ls -la "$XCFRAMEWORK_DIR/"
