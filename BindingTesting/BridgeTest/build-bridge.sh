#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Build the BridgeParamTestLib Swift bridge library
#
# Compiles the auto-generated bridge file (from the binding generator)
# plus test-only helpers into a framework dylib that can be referenced
# from the .NET test app via P/Invoke.

set -euo pipefail
cd "$(dirname "$0")"

# Platform check
if [ "$(uname -s)" != "Darwin" ]; then
    echo "Error: This script requires macOS (Darwin)."
    exit 1
fi

GENERATED_BRIDGE="output/Swift.BridgeParamTestLib.SwiftUIBridge.swift"
TEST_HELPERS="SwiftBridge/BridgeParamTestHelpers.swift"
XCFW_DIR=".build/BridgeParamTestLib.xcframework/ios-arm64-simulator"

# Ensure generated bridge exists
if [ ! -f "$GENERATED_BRIDGE" ]; then
    echo "Error: Generated bridge file not found: $GENERATED_BRIDGE"
    echo "Run ./regenerate-bindings.sh first."
    exit 1
fi

# Create framework directory
mkdir -p SwiftBridge/BridgeParamTestLibBridge.framework

SDK_PATH=$(xcrun --sdk iphonesimulator --show-sdk-path)

SOURCES="$GENERATED_BRIDGE"
if [ -f "$TEST_HELPERS" ]; then
    SOURCES="$SOURCES $TEST_HELPERS"
    echo "Compiling generated bridge + test helpers..."
else
    echo "Compiling generated bridge (no test helpers)..."
fi

xcrun swiftc -emit-library -target arm64-apple-ios16.0-simulator \
  -sdk "$SDK_PATH" \
  -F "$XCFW_DIR/" \
  -module-name BridgeParamTestLibBridge \
  -Xlinker -install_name -Xlinker @rpath/BridgeParamTestLibBridge.framework/BridgeParamTestLibBridge \
  -o SwiftBridge/BridgeParamTestLibBridge.framework/BridgeParamTestLibBridge \
  $SOURCES

# Create Info.plist for the framework bundle
cat > SwiftBridge/BridgeParamTestLibBridge.framework/Info.plist << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>com.swiftbindings.BridgeParamTestLibBridge</string>
    <key>CFBundleName</key>
    <string>BridgeParamTestLibBridge</string>
    <key>CFBundleExecutable</key>
    <string>BridgeParamTestLibBridge</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>MinimumOSVersion</key>
    <string>16.0</string>
    <key>CFBundleSupportedPlatforms</key>
    <array>
        <string>iPhoneSimulator</string>
    </array>
</dict>
</plist>
EOF

echo "BridgeParamTestLibBridge framework built successfully"
