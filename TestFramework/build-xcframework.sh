#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Builds the SwiftBindingsTestLib as an xcframework for iOS Simulator arm64.
# Produces: .build/SwiftBindingsTestLib.xcframework/
#
# Usage: ./build-xcframework.sh

set -e
cd "$(dirname "$0")"

MODULE_NAME="SwiftBindingsTestLib"
BUILD_DIR=".build"
SIM_BUILD_DIR="$BUILD_DIR/ios-simulator"
FRAMEWORK_DIR="$SIM_BUILD_DIR/$MODULE_NAME.framework"
XCFRAMEWORK_DIR="$BUILD_DIR/$MODULE_NAME.xcframework"

# SDK paths
SIM_SDK=$(xcrun --sdk iphonesimulator --show-sdk-path)
TARGET="arm64-apple-ios15.0-simulator"

echo "=== Building $MODULE_NAME ==="
echo "Target: $TARGET"
echo "SDK: $SIM_SDK"

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

# Compile with library evolution enabled
xcrun swiftc \
    -target "$TARGET" \
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

echo "=== Generating TBD ==="
xcrun tapi stubify \
    --filetype=tbd-v4 \
    "$FRAMEWORK_DIR/$MODULE_NAME" \
    -o "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/$MODULE_NAME.tbd"

echo "=== Generating ABI JSON ==="
xcrun swift-frontend \
    -compile-module-from-interface \
    "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios-simulator.swiftinterface" \
    -target "$TARGET" \
    -module-name "$MODULE_NAME" \
    -sdk "$SIM_SDK" \
    -emit-abi-descriptor-path "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/arm64-apple-ios-simulator.abi.json"

# Create framework Info.plist
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

echo "=== Creating xcframework ==="
rm -rf "$XCFRAMEWORK_DIR"
xcodebuild -create-xcframework \
    -framework "$FRAMEWORK_DIR" \
    -output "$XCFRAMEWORK_DIR"

echo ""
echo "=== Build Complete ==="
echo "xcframework: $XCFRAMEWORK_DIR"
echo ""
ls -la "$XCFRAMEWORK_DIR/"
