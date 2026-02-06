#!/bin/bash
# Build the BlinkIDUX Swift bridge library
#
# Compiles the auto-generated bridge file (from the binding generator)
# plus test-only helpers into a framework dylib that can be referenced
# from the .NET test app via P/Invoke.

set -euo pipefail

cd "$(dirname "$0")"

GENERATED_BRIDGE="output-ux/Swift.BlinkIDUX.SwiftUIBridge.swift"
TEST_HELPERS="SwiftBridge/BlinkIDUXBridgeTestHelpers.swift"

# Ensure generated bridge exists
if [ ! -f "$GENERATED_BRIDGE" ]; then
    echo "Error: Generated bridge file not found: $GENERATED_BRIDGE"
    echo "Run ./regenerate-ux-bindings.sh first."
    exit 1
fi

# Create framework directory
mkdir -p SwiftBridge/BlinkIDUXBridge.framework

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
  -F BlinkID.xcframework/ios-arm64_x86_64-simulator/ \
  -F BlinkIDUX.xcframework/ios-arm64_x86_64-simulator/ \
  -module-name BlinkIDUXBridge \
  -Xlinker -install_name -Xlinker @rpath/BlinkIDUXBridge.framework/BlinkIDUXBridge \
  -o SwiftBridge/BlinkIDUXBridge.framework/BlinkIDUXBridge \
  $SOURCES

# Create Info.plist for the framework bundle
cat > SwiftBridge/BlinkIDUXBridge.framework/Info.plist << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>com.swiftbindings.BlinkIDUXBridge</string>
    <key>CFBundleName</key>
    <string>BlinkIDUXBridge</string>
    <key>CFBundleExecutable</key>
    <string>BlinkIDUXBridge</string>
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

echo "BlinkIDUXBridge framework built successfully"
