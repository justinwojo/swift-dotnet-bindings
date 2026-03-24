#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Builds the SwiftBindingsTestLib as an xcframework.
# Supports iOS Simulator (default), macOS, and tvOS Simulator.
# Optionally includes a device slice for physical device deployment (iOS/tvOS only).
# Produces: .build/SwiftBindingsTestLib.xcframework/
#
# Usage: ./build-xcframework.sh [--platform ios|macos|tvos] [--include-device]
#
# Options:
#   --platform PLATFORM   Target platform: ios (default), macos, tvos
#   --include-device      Also build device slice (iOS/tvOS only; ignored for macOS)

set -e
cd "$(dirname "$0")"

PLATFORM="ios"
INCLUDE_DEVICE=false
while [[ $# -gt 0 ]]; do
    case $1 in
        --platform)
            PLATFORM="$2"
            shift 2
            ;;
        --include-device)
            INCLUDE_DEVICE=true
            shift
            ;;
        *)
            echo "Unknown option: $1"
            echo "Usage: ./build-xcframework.sh [--platform ios|macos|tvos] [--include-device]"
            exit 1
            ;;
    esac
done

# Validate platform
case "$PLATFORM" in
    ios|macos|tvos) ;;
    *)
        echo "Error: Unknown platform '$PLATFORM'. Must be ios, macos, or tvos."
        exit 1
        ;;
esac

# Platform-dependent variables
case "$PLATFORM" in
    ios)
        SIM_SDK_NAME="iphonesimulator"
        SIM_TARGET="arm64-apple-ios15.0-simulator"
        SIM_SLICE_ID="ios-arm64-simulator"
        SIM_MODULE_SUFFIX="arm64-apple-ios-simulator"
        SIM_PLIST_PLATFORM="iPhoneSimulator"
        DEVICE_SDK_NAME="iphoneos"
        DEVICE_TARGET="arm64-apple-ios15.0"
        DEVICE_SLICE_ID="ios-arm64"
        DEVICE_MODULE_SUFFIX="arm64-apple-ios"
        DEVICE_PLIST_PLATFORM="iPhoneOS"
        HAS_SIMULATOR=true
        PLIST_SUPPORTED_PLATFORM="ios"
        MIN_OS="15.0"
        ;;
    macos)
        SIM_SDK_NAME="macosx"
        SIM_TARGET="arm64-apple-macos12.0"
        SIM_SLICE_ID="macos-arm64"
        SIM_MODULE_SUFFIX="arm64-apple-macos"
        SIM_PLIST_PLATFORM="MacOSX"
        HAS_SIMULATOR=false
        PLIST_SUPPORTED_PLATFORM="macos"
        MIN_OS="12.0"
        ;;
    tvos)
        SIM_SDK_NAME="appletvsimulator"
        SIM_TARGET="arm64-apple-tvos15.0-simulator"
        SIM_SLICE_ID="tvos-arm64-simulator"
        SIM_MODULE_SUFFIX="arm64-apple-tvos-simulator"
        SIM_PLIST_PLATFORM="AppleTVSimulator"
        DEVICE_SDK_NAME="appletvos"
        DEVICE_TARGET="arm64-apple-tvos15.0"
        DEVICE_SLICE_ID="tvos-arm64"
        DEVICE_MODULE_SUFFIX="arm64-apple-tvos"
        DEVICE_PLIST_PLATFORM="AppleTVOS"
        HAS_SIMULATOR=true
        PLIST_SUPPORTED_PLATFORM="tvos"
        MIN_OS="15.0"
        ;;
esac

# macOS has no simulator/device distinction — ignore --include-device
if [ "$PLATFORM" = "macos" ] && [ "$INCLUDE_DEVICE" = true ]; then
    echo "Note: --include-device is ignored for macOS (single slice, no simulator/device distinction)."
    INCLUDE_DEVICE=false
fi

MODULE_NAME="SwiftBindingsTestLib"
DEP_MODULE_NAME="SwiftBindingsTestLibDependency"
BUILD_DIR=".build"
SIM_BUILD_DIR="$BUILD_DIR/$SIM_SLICE_ID"
FRAMEWORK_DIR="$SIM_BUILD_DIR/$MODULE_NAME.framework"
DEP_FRAMEWORK_DIR="$SIM_BUILD_DIR/$DEP_MODULE_NAME.framework"
DEVICE_BUILD_DIR="$BUILD_DIR/${DEVICE_SLICE_ID:-}"
DEVICE_FRAMEWORK_DIR="$DEVICE_BUILD_DIR/$MODULE_NAME.framework"
DEP_DEVICE_FRAMEWORK_DIR="$DEVICE_BUILD_DIR/$DEP_MODULE_NAME.framework"
XCFRAMEWORK_DIR="$BUILD_DIR/$MODULE_NAME.xcframework"
DEP_XCFRAMEWORK_DIR="$BUILD_DIR/$DEP_MODULE_NAME.xcframework"

# SDK paths
SIM_SDK=$(xcrun --sdk "$SIM_SDK_NAME" --show-sdk-path)

echo "=== Building $MODULE_NAME ==="
echo "Platform: $PLATFORM"
echo "Simulator target: $SIM_TARGET"
echo "Simulator SDK: $SIM_SDK"
[ "$INCLUDE_DEVICE" = true ] && echo "Device build: ENABLED"

# Clean previous build
rm -rf "$BUILD_DIR"
mkdir -p "$SIM_BUILD_DIR"

# --- Build dependency module first ---
echo ""
echo "--- Building dependency module: $DEP_MODULE_NAME ---"
mkdir -p "$DEP_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule"

DEP_SWIFT_FILES=$(find Sources/SwiftBindingsTestLibDependency -name '*.swift' -type f)
DEP_FILE_COUNT=$(echo "$DEP_SWIFT_FILES" | wc -l | tr -d ' ')
echo "Compiling $DEP_FILE_COUNT dependency source files..."

xcrun swiftc \
    -target "$SIM_TARGET" \
    -sdk "$SIM_SDK" \
    -emit-module \
    -emit-library \
    -enable-library-evolution \
    -emit-module-interface \
    -module-name "$DEP_MODULE_NAME" \
    -Xlinker -install_name -Xlinker "@rpath/$DEP_MODULE_NAME.framework/$DEP_MODULE_NAME" \
    -o "$DEP_FRAMEWORK_DIR/$DEP_MODULE_NAME" \
    -emit-module-path "$DEP_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/${SIM_MODULE_SUFFIX}.swiftmodule" \
    -emit-module-interface-path "$DEP_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/${SIM_MODULE_SUFFIX}.swiftinterface" \
    $DEP_SWIFT_FILES

cp "$DEP_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/${SIM_MODULE_SUFFIX}.swiftinterface" \
   "$DEP_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/${SIM_MODULE_SUFFIX}.private.swiftinterface"

# Dependency TBD
xcrun tapi stubify \
    --filetype=tbd-v4 \
    "$DEP_FRAMEWORK_DIR/$DEP_MODULE_NAME" \
    -o "$DEP_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/$DEP_MODULE_NAME.tbd"

# Dependency ABI JSON
xcrun swift-frontend \
    -compile-module-from-interface \
    "$DEP_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/${SIM_MODULE_SUFFIX}.swiftinterface" \
    -target "$SIM_TARGET" \
    -module-name "$DEP_MODULE_NAME" \
    -sdk "$SIM_SDK" \
    -emit-abi-descriptor-path "$DEP_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/${SIM_MODULE_SUFFIX}.abi.json"

# Dependency Info.plist
cat > "$DEP_FRAMEWORK_DIR/Info.plist" << 'DEPPLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>SwiftBindingsTestLibDependency</string>
    <key>CFBundleIdentifier</key>
    <string>com.test.SwiftBindingsTestLibDependency</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>SwiftBindingsTestLibDependency</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
</dict>
</plist>
DEPPLIST

echo "Dependency module built: $DEP_FRAMEWORK_DIR"

# --- Build main module ---
mkdir -p "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule"

# Collect Swift source files (excluding disabled directories and files)
# This matches the exclusions in Package.swift
SWIFT_FILES=$(find Sources/SwiftBindingsTestLib \
    -type d -name '*.disabled' -prune -o \
    -name '*.swift' -type f -print \
    | grep -v 'Closures/Autoclosures\.swift$' \
    | grep -v 'UnsafeTypes/Span\.swift$' \
    | grep -v 'UnsafeTypes/PointerGenerics\.swift$' \
    | grep -v 'Foundation/Date\.swift$')
FILE_COUNT=$(echo "$SWIFT_FILES" | wc -l | tr -d ' ')
echo "Compiling $FILE_COUNT Swift source files..."

# Compile simulator slice with library evolution enabled
echo ""
echo "--- Compiling ${PLATFORM} slice ---"
xcrun swiftc \
    -target "$SIM_TARGET" \
    -sdk "$SIM_SDK" \
    -emit-module \
    -emit-library \
    -enable-library-evolution \
    -emit-module-interface \
    -module-name "$MODULE_NAME" \
    -F "$SIM_BUILD_DIR" \
    -Xlinker -install_name -Xlinker "@rpath/$MODULE_NAME.framework/$MODULE_NAME" \
    -o "$FRAMEWORK_DIR/$MODULE_NAME" \
    -emit-module-path "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/${SIM_MODULE_SUFFIX}.swiftmodule" \
    -emit-module-interface-path "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/${SIM_MODULE_SUFFIX}.swiftinterface" \
    $SWIFT_FILES

# Copy private swiftinterface (same as public for our purposes)
cp "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/${SIM_MODULE_SUFFIX}.swiftinterface" \
   "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/${SIM_MODULE_SUFFIX}.private.swiftinterface"

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

echo "=== Generating TBD (${PLATFORM}) ==="
xcrun tapi stubify \
    --filetype=tbd-v4 \
    "$FRAMEWORK_DIR/$MODULE_NAME" \
    -o "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/$MODULE_NAME.tbd"

echo "=== Generating ABI JSON (${PLATFORM}) ==="
xcrun swift-frontend \
    -compile-module-from-interface \
    "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/${SIM_MODULE_SUFFIX}.swiftinterface" \
    -target "$SIM_TARGET" \
    -module-name "$MODULE_NAME" \
    -sdk "$SIM_SDK" \
    -F "$SIM_BUILD_DIR" \
    -emit-abi-descriptor-path "$FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/${SIM_MODULE_SUFFIX}.abi.json"

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

# --- Device slice (optional, iOS/tvOS only) ---
if [ "$INCLUDE_DEVICE" = true ]; then
    DEVICE_SDK=$(xcrun --sdk "$DEVICE_SDK_NAME" --show-sdk-path)

    echo ""
    echo "--- Compiling device slice ---"
    echo "Device target: $DEVICE_TARGET"
    echo "Device SDK: $DEVICE_SDK"

    mkdir -p "$DEVICE_BUILD_DIR"

    # Build dependency device slice
    mkdir -p "$DEP_DEVICE_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule"

    xcrun swiftc \
        -target "$DEVICE_TARGET" \
        -sdk "$DEVICE_SDK" \
        -emit-module \
        -emit-library \
        -enable-library-evolution \
        -emit-module-interface \
        -module-name "$DEP_MODULE_NAME" \
        -Xlinker -install_name -Xlinker "@rpath/$DEP_MODULE_NAME.framework/$DEP_MODULE_NAME" \
        -o "$DEP_DEVICE_FRAMEWORK_DIR/$DEP_MODULE_NAME" \
        -emit-module-path "$DEP_DEVICE_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/${DEVICE_MODULE_SUFFIX}.swiftmodule" \
        -emit-module-interface-path "$DEP_DEVICE_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/${DEVICE_MODULE_SUFFIX}.swiftinterface" \
        $DEP_SWIFT_FILES

    cp "$DEP_DEVICE_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/${DEVICE_MODULE_SUFFIX}.swiftinterface" \
       "$DEP_DEVICE_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/${DEVICE_MODULE_SUFFIX}.private.swiftinterface"

    xcrun tapi stubify \
        --filetype=tbd-v4 \
        "$DEP_DEVICE_FRAMEWORK_DIR/$DEP_MODULE_NAME" \
        -o "$DEP_DEVICE_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/$DEP_MODULE_NAME.tbd"

    xcrun swift-frontend \
        -compile-module-from-interface \
        "$DEP_DEVICE_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/${DEVICE_MODULE_SUFFIX}.swiftinterface" \
        -target "$DEVICE_TARGET" \
        -module-name "$DEP_MODULE_NAME" \
        -sdk "$DEVICE_SDK" \
        -emit-abi-descriptor-path "$DEP_DEVICE_FRAMEWORK_DIR/Modules/$DEP_MODULE_NAME.swiftmodule/${DEVICE_MODULE_SUFFIX}.abi.json"

    cat > "$DEP_DEVICE_FRAMEWORK_DIR/Info.plist" << 'DEPDPLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>SwiftBindingsTestLibDependency</string>
    <key>CFBundleIdentifier</key>
    <string>com.test.SwiftBindingsTestLibDependency</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>SwiftBindingsTestLibDependency</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
</dict>
</plist>
DEPDPLIST

    # Build main module device slice
    mkdir -p "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule"

    xcrun swiftc \
        -target "$DEVICE_TARGET" \
        -sdk "$DEVICE_SDK" \
        -emit-module \
        -emit-library \
        -enable-library-evolution \
        -emit-module-interface \
        -module-name "$MODULE_NAME" \
        -F "$DEVICE_BUILD_DIR" \
        -Xlinker -install_name -Xlinker "@rpath/$MODULE_NAME.framework/$MODULE_NAME" \
        -o "$DEVICE_FRAMEWORK_DIR/$MODULE_NAME" \
        -emit-module-path "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/${DEVICE_MODULE_SUFFIX}.swiftmodule" \
        -emit-module-interface-path "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/${DEVICE_MODULE_SUFFIX}.swiftinterface" \
        $SWIFT_FILES

    cp "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/${DEVICE_MODULE_SUFFIX}.swiftinterface" \
       "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/${DEVICE_MODULE_SUFFIX}.private.swiftinterface"

    echo "=== Generating TBD (device) ==="
    xcrun tapi stubify \
        --filetype=tbd-v4 \
        "$DEVICE_FRAMEWORK_DIR/$MODULE_NAME" \
        -o "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/$MODULE_NAME.tbd"

    echo "=== Generating ABI JSON (device) ==="
    xcrun swift-frontend \
        -compile-module-from-interface \
        "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/${DEVICE_MODULE_SUFFIX}.swiftinterface" \
        -target "$DEVICE_TARGET" \
        -module-name "$MODULE_NAME" \
        -sdk "$DEVICE_SDK" \
        -F "$DEVICE_BUILD_DIR" \
        -emit-abi-descriptor-path "$DEVICE_FRAMEWORK_DIR/Modules/$MODULE_NAME.swiftmodule/${DEVICE_MODULE_SUFFIX}.abi.json"

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

echo "=== Creating xcframeworks ==="

# Create dependency xcframework
rm -rf "$DEP_XCFRAMEWORK_DIR"
if [ "$INCLUDE_DEVICE" = true ]; then
    xcodebuild -create-xcframework \
        -framework "$DEP_FRAMEWORK_DIR" \
        -framework "$DEP_DEVICE_FRAMEWORK_DIR" \
        -output "$DEP_XCFRAMEWORK_DIR"
else
    xcodebuild -create-xcframework \
        -framework "$DEP_FRAMEWORK_DIR" \
        -output "$DEP_XCFRAMEWORK_DIR"
fi

# Create main xcframework
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
echo "Platform: $PLATFORM"
echo "xcframeworks: $XCFRAMEWORK_DIR, $DEP_XCFRAMEWORK_DIR"
[ "$INCLUDE_DEVICE" = true ] && echo "Slices: simulator + device"
echo ""
ls -la "$XCFRAMEWORK_DIR/"
echo ""
ls -la "$DEP_XCFRAMEWORK_DIR/"
