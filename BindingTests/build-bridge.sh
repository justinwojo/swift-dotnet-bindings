#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Build the SwiftBindingsTestLib Swift bridge library
#
# Compiles the auto-generated bridge file (from the binding generator)
# plus test-only helpers into a framework dylib that can be referenced
# from the .NET test app via P/Invoke.
#
# Usage: ./build-bridge.sh [--platform ios|macos|tvos]
#
# Options:
#   --platform PLATFORM   Target platform: ios (default), macos, tvos

set -euo pipefail
cd "$(dirname "$0")"

# Platform check
if [ "$(uname -s)" != "Darwin" ]; then
    echo "Error: This script requires macOS (Darwin)."
    exit 1
fi

PLATFORM="ios"
while [[ $# -gt 0 ]]; do
    case $1 in
        --platform)
            PLATFORM="$2"
            shift 2
            ;;
        *)
            echo "Unknown option: $1"
            echo "Usage: ./build-bridge.sh [--platform ios|macos|tvos]"
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
        SLICE_ID="ios-arm64-simulator"
        SDK_NAME="iphonesimulator"
        TARGET_TRIPLE="arm64-apple-ios15.0-simulator"
        PLIST_PLATFORM="iPhoneSimulator"
        MIN_OS="15.0"
        ;;
    macos)
        SLICE_ID="macos-arm64"
        SDK_NAME="macosx"
        TARGET_TRIPLE="arm64-apple-macos12.0"
        PLIST_PLATFORM="MacOSX"
        MIN_OS="12.0"
        ;;
    tvos)
        SLICE_ID="tvos-arm64-simulator"
        SDK_NAME="appletvsimulator"
        TARGET_TRIPLE="arm64-apple-tvos15.0-simulator"
        PLIST_PLATFORM="AppleTVSimulator"
        MIN_OS="15.0"
        ;;
esac

MODULE_NAME="SwiftBindingsTestLib"
BRIDGE_MODULE="SwiftBindingsTestLibBridge"
GENERATED_BRIDGE="output/${MODULE_NAME}.SwiftUIBridge.swift"
TEST_HELPERS="SwiftBridge/SwiftUIBridgeTestHelpers.swift"
XCFW_DIR=".build/${MODULE_NAME}.xcframework/$SLICE_ID"
OUTPUT_DIR="SwiftBridge/${BRIDGE_MODULE}.framework"

# Ensure generated bridge exists
if [ ! -f "$GENERATED_BRIDGE" ]; then
    echo "Error: Generated bridge file not found: $GENERATED_BRIDGE"
    echo "Run ./regenerate-bindings.sh first."
    exit 1
fi

# Smoke check: verify expected @_cdecl entrypoints exist in generated bridge
echo "Verifying generated bridge shape..."
EXPECTED_SYMBOLS=(
    "SBW_${MODULE_NAME}_EnumParamView_Create"
    "SBW_${MODULE_NAME}_EnumParamView_GetViewController"
    "SBW_${MODULE_NAME}_EnumParamView_Free"
)
for sym in "${EXPECTED_SYMBOLS[@]}"; do
    if ! grep -q "$sym" "$GENERATED_BRIDGE"; then
        echo "Error: Expected @_cdecl entrypoint not found: $sym"
        echo "The generated bridge shape has changed. Check emitter output."
        exit 1
    fi
done
echo "Bridge shape verified."

# Create framework directory
mkdir -p "$OUTPUT_DIR"

SDK_PATH=$(xcrun --sdk "$SDK_NAME" --show-sdk-path)

SOURCES="$GENERATED_BRIDGE"
if [ -f "$TEST_HELPERS" ]; then
    SOURCES="$SOURCES $TEST_HELPERS"
    echo "Compiling generated bridge + test helpers..."
else
    echo "Compiling generated bridge (no test helpers)..."
fi

xcrun swiftc -emit-library -target "$TARGET_TRIPLE" \
  -sdk "$SDK_PATH" \
  -F "$XCFW_DIR/" \
  -module-name "$BRIDGE_MODULE" \
  -Xlinker -install_name -Xlinker "@rpath/${BRIDGE_MODULE}.framework/${BRIDGE_MODULE}" \
  -o "$OUTPUT_DIR/$BRIDGE_MODULE" \
  $SOURCES

# Create Info.plist for the framework bundle
cat > "$OUTPUT_DIR/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>com.swiftbindings.${BRIDGE_MODULE}</string>
    <key>CFBundleName</key>
    <string>${BRIDGE_MODULE}</string>
    <key>CFBundleExecutable</key>
    <string>${BRIDGE_MODULE}</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>MinimumOSVersion</key>
    <string>${MIN_OS}</string>
    <key>CFBundleSupportedPlatforms</key>
    <array>
        <string>${PLIST_PLATFORM}</string>
    </array>
</dict>
</plist>
EOF

echo "${BRIDGE_MODULE} framework built successfully"
