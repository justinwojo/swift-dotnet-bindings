#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Build the Lottie SwiftUI bridge framework
#
# Compiles the auto-generated bridge file (from the binding generator)
# into a framework dylib that can be referenced from the .NET test app
# via P/Invoke.

set -euo pipefail
cd "$(dirname "$0")"

# Platform check
if [ "$(uname -s)" != "Darwin" ]; then
    echo "Error: This script requires macOS (Darwin)."
    exit 1
fi

GENERATED_BRIDGE="output-ios/Swift.Lottie.SwiftUIBridge.swift"

# Ensure generated bridge exists
if [ ! -f "$GENERATED_BRIDGE" ]; then
    echo "Error: Generated bridge file not found: $GENERATED_BRIDGE"
    echo "Run ./regenerate-bindings.sh first."
    exit 1
fi

# Create framework directory
mkdir -p output-ios/LottieBridge.framework

SDK_PATH=$(xcrun --sdk iphonesimulator --show-sdk-path)

echo "Compiling generated SwiftUI bridge..."

xcrun swiftc -emit-library -target arm64-apple-ios15.0-simulator \
  -sdk "$SDK_PATH" \
  -F Lottie.xcframework/ios-arm64_x86_64-simulator/ \
  -module-name LottieBridge \
  -Xlinker -install_name -Xlinker @rpath/LottieBridge.framework/LottieBridge \
  -o output-ios/LottieBridge.framework/LottieBridge \
  "$GENERATED_BRIDGE"

# Create Info.plist for the framework bundle
cat > output-ios/LottieBridge.framework/Info.plist << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>com.swiftbindings.LottieBridge</string>
    <key>CFBundleName</key>
    <string>LottieBridge</string>
    <key>CFBundleExecutable</key>
    <string>LottieBridge</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>MinimumOSVersion</key>
    <string>15.0</string>
    <key>CFBundleSupportedPlatforms</key>
    <array>
        <string>iPhoneSimulator</string>
    </array>
</dict>
</plist>
EOF

echo "LottieBridge framework built successfully"
